using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Cascade.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Shopify;

public interface IShopifyAdminClient
{
    IAsyncEnumerable<OrderSnapshot> OrdersUpdatedSinceAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>GraphQL Admin API (REST Admin is legacy). Pages through orders filtered on updated_at.</summary>
public sealed class ShopifyAdminClient(HttpClient http, ShopifyOptions options) : IShopifyAdminClient
{
    private const string Query = """
        query($q: String!, $after: String) {
          orders(first: 100, after: $after, query: $q, sortKey: UPDATED_AT) {
            pageInfo { hasNextPage endCursor }
            nodes {
              legacyResourceId name displayFinancialStatus displayFulfillmentStatus cancelledAt updatedAt
              totalPriceSet { shopMoney { amount currencyCode } }
            }
          }
        }
        """;

    public async IAsyncEnumerable<OrderSnapshot> OrdersUpdatedSinceAsync(DateTimeOffset since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var shop = options.ShopDomain!;
        var url = $"https://{shop}/admin/api/{options.AdminApiVersion}/graphql.json";
        string? after = null;
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { query = Query, variables = new { q = $"updated_at:>='{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'", after } }),
            };
            request.Headers.Add("X-Shopify-Access-Token", options.AdminAccessToken);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("errors", out var errors))
                throw new InvalidOperationException($"Shopify GraphQL error: {errors}");

            var orders = doc.RootElement.GetProperty("data").GetProperty("orders");
            foreach (var n in orders.GetProperty("nodes").EnumerateArray())
            {
                var money = n.GetProperty("totalPriceSet").GetProperty("shopMoney");
                yield return new OrderSnapshot(
                    shop,
                    long.Parse(n.GetProperty("legacyResourceId").GetString()!, CultureInfo.InvariantCulture),
                    n.GetProperty("name").GetString() ?? "",
                    NormaliseEnum(n.GetProperty("displayFinancialStatus").GetString()) ?? "unknown",
                    NormaliseEnum(n.GetProperty("displayFulfillmentStatus").GetString()),
                    n.GetProperty("cancelledAt").ValueKind == JsonValueKind.String,
                    decimal.Parse(money.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture),
                    money.GetProperty("currencyCode").GetString()!,
                    n.GetProperty("updatedAt").GetDateTimeOffset().ToUniversalTime());
            }

            var page = orders.GetProperty("pageInfo");
            after = page.GetProperty("hasNextPage").GetBoolean() ? page.GetProperty("endCursor").GetString() : null;
        } while (after is not null);
    }

    // GraphQL says PARTIALLY_REFUNDED, webhooks say partially_refunded. Store the webhook spelling.
    // (Fulfillment enums differ more than case — e.g. UNFULFILLED vs null — which is fine for display, not for logic.)
    private static string? NormaliseEnum(string? value) => value?.ToLowerInvariant();
}

/// <summary>
/// Webhooks are not guaranteed. Periodically fetch everything updated since the last run (minus an overlap) and push
/// it through the same monotonic projector. Anything that changes state here is an event the webhooks missed or have
/// not delivered yet; the run records that count.
/// </summary>
public sealed class ShopifyReconciler(CascadeDbContext db, IShopifyAdminClient admin, ShopifyOrderProjector projector, ShopifyOptions options, TimeProvider clock, ILogger<ShopifyReconciler> log)
{
    public async Task<ShopifyReconciliationRun> RunAsync(CancellationToken ct)
    {
        var shop = options.ShopDomain ?? throw new InvalidOperationException("Shopify:ShopDomain is not configured.");
        var cursorName = $"shopify-orders:{shop}";
        var cursor = await db.SyncCursors.FindAsync([cursorName], ct);
        var startedAt = clock.GetUtcNow();
        var since = cursor is null ? startedAt - TimeSpan.FromDays(1) : cursor.Value - options.ReconcileOverlap;

        var run = new ShopifyReconciliationRun { Id = Guid.CreateVersion7(startedAt), ShopDomain = shop, StartedAt = startedAt, Since = since };
        var maxSeen = cursor?.Value ?? since;
        try
        {
            await foreach (var order in admin.OrdersUpdatedSinceAsync(since, ct))
            {
                run.Fetched++;
                if (await projector.ApplyAsync(order, "reconciliation", ct) != ProjectionResult.Stale) run.Repaired++;
                if (order.UpdatedAt > maxSeen) maxSeen = order.UpdatedAt;
            }

            if (cursor is null) db.SyncCursors.Add(new SyncCursor { Name = cursorName, Value = maxSeen });
            else cursor.Value = maxSeen;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Cursor is not advanced; the next run covers the same window again.
            run.Error = ex.Message;
            log.LogError(ex, "Shopify reconciliation for {Shop} failed", shop);
        }

        run.FinishedAt = clock.GetUtcNow();
        db.ShopifyReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        if (run.Repaired > 0) log.LogWarning("Reconciliation repaired {Repaired} of {Fetched} orders for {Shop}", run.Repaired, run.Fetched, shop);
        return run;
    }
}
