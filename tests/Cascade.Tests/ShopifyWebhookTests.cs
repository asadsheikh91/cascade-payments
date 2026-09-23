using System.Net;
using System.Text;
using Cascade.Api.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cascade.Tests;

[Collection(DbCollection.Name)]
public class ShopifyWebhookTests(CascadeFactory factory)
{
    private readonly HttpClient _http = factory.CreateClient();
    private static long _nextOrderId = 5_000_000_000 + Random.Shared.Next(1_000_000);

    private static string OrderJson(long id, string financialStatus, DateTimeOffset updatedAt) =>
        // Deliberately odd spacing: the signature is over these exact bytes.
        $$"""{"id":{{id}},  "name":"#{{id % 10000}}","financial_status":"{{financialStatus}}","fulfillment_status":null,"cancelled_at":null,"total_price":"49.90","currency":"EUR","updated_at":"{{updatedAt:yyyy-MM-ddTHH:mm:sszzz}}"}""";

    private async Task<HttpResponseMessage> SendAsync(string body, string webhookId, string? eventId = null, string topic = "orders/updated",
        DateTimeOffset? triggeredAt = null, string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/shopify") { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.Add("X-Shopify-Hmac-SHA256", signature ?? ShopifyHmac.Sign(bytes, CascadeFactory.WebhookSecret));
        req.Headers.Add("X-Shopify-Topic", topic);
        req.Headers.Add("X-Shopify-Shop-Domain", CascadeFactory.Shop);
        req.Headers.Add("X-Shopify-Webhook-Id", webhookId);
        req.Headers.Add("X-Shopify-API-Version", "2026-07");
        if (eventId is not null) req.Headers.Add("X-Shopify-Event-Id", eventId);
        if (triggeredAt is not null) req.Headers.Add("X-Shopify-Triggered-At", triggeredAt.Value.ToString("O"));
        return await _http.SendAsync(req);
    }

    private Task<int> ProcessInboxAsync() =>
        factory.WithScopeAsync(sp => sp.GetRequiredService<ShopifyInboxProcessor>().ProcessBatchAsync(1000, CancellationToken.None));

    private Task<WebhookDelivery?> DeliveryAsync(string webhookId) =>
        factory.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.WebhookId == webhookId));

    private Task<ShopifyOrder?> OrderAsync(long id) =>
        factory.WithDbAsync(db => db.ShopifyOrders.AsNoTracking().SingleOrDefaultAsync(o => o.ShopDomain == CascadeFactory.Shop && o.Id == id));

    [Fact]
    public async Task Invalid_signature_is_rejected_and_nothing_is_stored()
    {
        var id = Guid.NewGuid().ToString();
        var res = await SendAsync(OrderJson(1, "paid", DateTimeOffset.UtcNow), id, signature: Convert.ToBase64String(new byte[32]));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(await DeliveryAsync(id));
    }

    [Fact]
    public async Task Valid_delivery_is_acknowledged_then_processed_asynchronously()
    {
        var orderId = Interlocked.Increment(ref _nextOrderId);
        var webhookId = Guid.NewGuid().ToString();

        var res = await SendAsync(OrderJson(orderId, "paid", DateTimeOffset.UtcNow), webhookId, eventId: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(DeliveryStatus.Pending, (await DeliveryAsync(webhookId))!.Status);
        Assert.Null(await OrderAsync(orderId)); // not processed inline

        await ProcessInboxAsync();

        Assert.Equal(DeliveryStatus.Processed, (await DeliveryAsync(webhookId))!.Status);
        Assert.Equal("paid", (await OrderAsync(orderId))!.FinancialStatus);
    }

    [Fact]
    public async Task Retried_delivery_with_same_webhook_id_is_stored_once()
    {
        var webhookId = Guid.NewGuid().ToString();
        var body = OrderJson(Interlocked.Increment(ref _nextOrderId), "paid", DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(body, webhookId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(body, webhookId)).StatusCode);

        Assert.Equal(1, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync(d => d.WebhookId == webhookId)));
    }

    [Fact]
    public async Task Same_event_via_two_subscriptions_is_processed_once()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = OrderJson(Interlocked.Increment(ref _nextOrderId), "paid", DateTimeOffset.UtcNow);
        var (w1, w2) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        await SendAsync(body, w1, eventId);
        await SendAsync(body, w2, eventId);
        await ProcessInboxAsync();

        var statuses = new[] { (await DeliveryAsync(w1))!.Status, (await DeliveryAsync(w2))!.Status };
        Assert.Contains(DeliveryStatus.Processed, statuses);
        Assert.Contains(DeliveryStatus.DuplicateEvent, statuses);
    }

    [Fact]
    public async Task Out_of_order_delivery_does_not_regress_the_order()
    {
        var orderId = Interlocked.Increment(ref _nextOrderId);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);

        // "paid" (newer) arrives and is processed before "pending" (older).
        await SendAsync(OrderJson(orderId, "paid", t0.AddMinutes(1)), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), triggeredAt: t0.AddMinutes(1));
        await ProcessInboxAsync();
        await SendAsync(OrderJson(orderId, "pending", t0), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), topic: "orders/create", triggeredAt: t0);
        await ProcessInboxAsync();

        Assert.Equal("paid", (await OrderAsync(orderId))!.FinancialStatus);
    }

    [Fact]
    public async Task Poison_delivery_is_retried_with_backoff_not_lost()
    {
        var webhookId = Guid.NewGuid().ToString();
        await SendAsync("""{"unexpected":"shape"}""", webhookId, Guid.NewGuid().ToString());
        await ProcessInboxAsync();

        var d = (await DeliveryAsync(webhookId))!;
        Assert.Equal(DeliveryStatus.Pending, d.Status);
        Assert.Equal(1, d.Attempts);
        Assert.NotNull(d.LastError);
        Assert.True(d.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Reconciliation_repairs_a_missed_webhook_and_is_idempotent()
    {
        var orderId = Interlocked.Increment(ref _nextOrderId);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-3);

        await SendAsync(OrderJson(orderId, "pending", t0), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await ProcessInboxAsync();

        // The orders/paid webhook never arrives. Shopify's API knows the order is paid.
        factory.ShopifyAdmin.Orders.Add(new OrderSnapshot(CascadeFactory.Shop, orderId, "#x", "paid", null, false, 49.90m, "EUR", t0.AddMinutes(1)));
        try
        {
            var first = await factory.WithScopeAsync(sp => sp.GetRequiredService<ShopifyReconciler>().RunAsync(CancellationToken.None));
            var order = (await OrderAsync(orderId))!;
            Assert.Null(first.Error);
            Assert.True(first.Repaired >= 1);
            Assert.Equal("paid", order.FinancialStatus);
            Assert.Equal("reconciliation", order.LastSource);

            var second = await factory.WithScopeAsync(sp => sp.GetRequiredService<ShopifyReconciler>().RunAsync(CancellationToken.None));
            Assert.Equal(0, second.Repaired);
        }
        finally
        {
            factory.ShopifyAdmin.Orders.Clear();
        }
    }
}
