using System.Globalization;
using System.Text.Json;
using Cascade.Api.Data;

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
public sealed class ShopifyOrderProjector(CascadeDbContext db, IDatabaseDialect dialect, TimeProvider clock)
{
    // Single statement: insert, or update only when strictly newer. No read-then-write race between workers.
    public Task<ProjectionResult> ApplyAsync(OrderSnapshot s, string source, CancellationToken ct) =>
        dialect.UpsertOrderIfNewerAsync(db, s, clock.GetUtcNow(), source, ct);
}
