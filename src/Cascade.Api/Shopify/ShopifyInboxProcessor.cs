using System.Text.Json;
using Cascade.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Shopify;

/// <summary>
/// Drains the webhook inbox. Each delivery is claimed with FOR UPDATE SKIP LOCKED inside its own transaction, so
/// several API replicas can run this safely and a poison message only blocks itself.
/// </summary>
public sealed class ShopifyInboxProcessor(CascadeDbContext db, ShopifyOrderProjector projector, TimeProvider clock, ILogger<ShopifyInboxProcessor> log)
{
    public const int MaxAttempts = 10;

    private static readonly HashSet<string> OrderTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "orders/create", "orders/updated", "orders/paid", "orders/cancelled", "orders/fulfilled", "orders/partially_fulfilled",
    };

    /// <returns>Number of deliveries handled (0 when the inbox is empty).</returns>
    public async Task<int> ProcessBatchAsync(int max, CancellationToken ct)
    {
        var handled = 0;
        while (handled < max && await ProcessOneAsync(ct)) handled++;
        return handled;
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = nameof(DeliveryStatus.Pending);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var delivery = await db.WebhookDeliveries
            .FromSql($"""
                SELECT * FROM webhook_deliveries
                WHERE status = {pending} AND next_attempt_at <= {now}
                ORDER BY triggered_at NULLS LAST, received_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(ct);
        if (delivery is null) return false;

        try
        {
            delivery.Status = await HandleAsync(delivery, ct);
            delivery.ProcessedAt = clock.GetUtcNow();
            delivery.LastError = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            await RecordFailureAsync(delivery.WebhookId, ex, CancellationToken.None);
        }
        return true;
    }

    private async Task<DeliveryStatus> HandleAsync(WebhookDelivery d, CancellationToken ct)
    {
        // Second line of defence after the webhook_id primary key: the same merchant action delivered through a second
        // subscription has a new webhook id but the same event id (ADR 0001).
        var eventKey = d.EventKey;
        if (await db.ProcessedShopifyEvents.AnyAsync(e => e.IdempotencyKey == eventKey, ct))
        {
            log.LogInformation("Delivery {WebhookId} repeats already-processed event {EventKey}", d.WebhookId, eventKey);
            return DeliveryStatus.DuplicateEvent;
        }

        DeliveryStatus status;
        if (OrderTopics.Contains(d.Topic))
        {
            using var doc = JsonDocument.Parse(d.Payload);
            var result = await projector.ApplyAsync(OrderSnapshot.FromWebhook(d.ShopDomain, doc.RootElement), "webhook", ct);
            log.LogInformation("Delivery {WebhookId} ({Topic}) -> order {Result}", d.WebhookId, d.Topic, result);
            status = DeliveryStatus.Processed;
        }
        else
        {
            status = DeliveryStatus.Ignored;
        }

        db.ProcessedShopifyEvents.Add(new ProcessedShopifyEvent { IdempotencyKey = eventKey, WebhookId = d.WebhookId, ProcessedAt = clock.GetUtcNow() });
        return status;
    }

    private async Task RecordFailureAsync(string webhookId, Exception ex, CancellationToken ct)
    {
        var d = await db.WebhookDeliveries.SingleAsync(x => x.WebhookId == webhookId, ct);
        d.Attempts++;
        d.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        if (d.Attempts >= MaxAttempts)
        {
            d.Status = DeliveryStatus.Dead;
            log.LogError(ex, "Delivery {WebhookId} dead-lettered after {Attempts} attempts", webhookId, d.Attempts);
        }
        else
        {
            d.NextAttemptAt = clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, d.Attempts)));
            log.LogWarning(ex, "Delivery {WebhookId} failed (attempt {Attempts}), retrying at {Next}", webhookId, d.Attempts, d.NextAttemptAt);
        }
        await db.SaveChangesAsync(ct);
    }
}
