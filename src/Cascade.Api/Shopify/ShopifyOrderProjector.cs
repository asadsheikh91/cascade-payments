using System.Globalization;
using System.Text.Json;
using Cascade.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Shopify;

public sealed record OrderSnapshot(
    string ShopDomain, long Id, string Name, string FinancialStatus, string? FulfillmentStatus,
    bool Cancelled, decimal TotalPrice, string Currency, DateTimeOffset UpdatedAt)
{
    /// <summary>Parses the REST-shaped order payload that orders/* webhooks carry.</summary>
    public static OrderSnapshot FromWebhook(string shopDomain, JsonElement o) => new(
        shopDomain,
        o.GetProperty("id").GetInt64(),
        o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        o.TryGetProperty("financial_status", out var fs) && fs.ValueKind == JsonValueKind.String ? fs.GetString()! : "unknown",
        o.TryGetProperty("fulfillment_status", out var ful) && ful.ValueKind == JsonValueKind.String ? ful.GetString() : null,
        o.TryGetProperty("cancelled_at", out var c) && c.ValueKind == JsonValueKind.String,
        decimal.Parse(o.GetProperty("total_price").GetString()!, CultureInfo.InvariantCulture),
        o.GetProperty("currency").GetString()!,
        o.GetProperty("updated_at").GetDateTimeOffset().ToUniversalTime());
}

public enum ProjectionResult { Created, Updated, Stale }

/// <summary>
/// Shopify does not guarantee delivery order. Sorting by X-Shopify-Triggered-At reduces churn, but correctness comes
/// from here: a snapshot is applied only if its updated_at is newer than what we hold, so an older event arriving late
/// is a no-op. The same rule makes webhooks and the reconciliation fetch safe to interleave.
/// </summary>
public sealed class ShopifyOrderProjector(CascadeDbContext db, TimeProvider clock)
{
    public async Task<ProjectionResult> ApplyAsync(OrderSnapshot s, string source, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Single statement: insert, or update only when strictly newer. No read-then-write race between workers.
        var affected = await db.Database.SqlQuery<bool>($"""
            INSERT INTO shopify_orders AS o (shop_domain, id, name, financial_status, fulfillment_status, cancelled, total_price, currency, shopify_updated_at, last_synced_at, last_source)
            VALUES ({s.ShopDomain}, {s.Id}, {s.Name}, {s.FinancialStatus}, {s.FulfillmentStatus}, {s.Cancelled}, {s.TotalPrice}, {s.Currency}, {s.UpdatedAt}, {now}, {source})
            ON CONFLICT (shop_domain, id) DO UPDATE SET
                name = EXCLUDED.name,
                financial_status = EXCLUDED.financial_status,
                fulfillment_status = EXCLUDED.fulfillment_status,
                cancelled = EXCLUDED.cancelled,
                total_price = EXCLUDED.total_price,
                currency = EXCLUDED.currency,
                shopify_updated_at = EXCLUDED.shopify_updated_at,
                last_synced_at = EXCLUDED.last_synced_at,
                last_source = EXCLUDED.last_source
            WHERE o.shopify_updated_at < EXCLUDED.shopify_updated_at
            RETURNING (xmax = 0) AS "Value"
            """).ToListAsync(ct);

        return affected.Count == 0 ? ProjectionResult.Stale : affected[0] ? ProjectionResult.Created : ProjectionResult.Updated;
    }
}
