using System.Data;
using Cascade.Api.Shopify;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cascade.Api.Data;

/// <summary>
/// The few statements EF Core cannot express portably: insert-if-absent, a conditional upsert, a skip-locked queue
/// claim, and recognising a unique-key violation. Everything else goes through LINQ and runs unchanged on both
/// databases. See ADR 0003 for how each Postgres construct maps to SQL Server.
/// </summary>
public interface IDatabaseDialect
{
    bool IsUniqueViolation(DbUpdateException ex);

    /// <summary>Next due delivery, row-locked for the current transaction; rows locked by another worker are skipped.</summary>
    IQueryable<WebhookDelivery> ClaimNextDelivery(CascadeDbContext db, DateTimeOffset now);

    /// <summary>Inserts the delivery unless its webhook id is already stored. Returns rows inserted (0 or 1).</summary>
    Task<int> InsertDeliveryIfNewAsync(CascadeDbContext db, WebhookDelivery d, CancellationToken ct);

    /// <summary>Single statement: insert, or update only when the snapshot is strictly newer than the stored row.</summary>
    Task<ProjectionResult> UpsertOrderIfNewerAsync(CascadeDbContext db, OrderSnapshot s, DateTimeOffset now, string source, CancellationToken ct);
}

public sealed class PostgresDialect : IDatabaseDialect
{
    public bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public IQueryable<WebhookDelivery> ClaimNextDelivery(CascadeDbContext db, DateTimeOffset now)
    {
        var pending = nameof(DeliveryStatus.Pending);
        return db.WebhookDeliveries.FromSql($"""
            SELECT * FROM webhook_deliveries
            WHERE status = {pending} AND next_attempt_at <= {now}
            ORDER BY triggered_at NULLS LAST, received_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """);
    }

    public Task<int> InsertDeliveryIfNewAsync(CascadeDbContext db, WebhookDelivery d, CancellationToken ct)
    {
        var status = d.Status.ToString();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO webhook_deliveries (webhook_id, event_id, topic, shop_domain, api_version, triggered_at, received_at, payload, status, attempts, next_attempt_at)
            VALUES ({d.WebhookId}, {d.EventId}, {d.Topic}, {d.ShopDomain}, {d.ApiVersion}, {d.TriggeredAt}, {d.ReceivedAt}, {d.Payload}::jsonb, {status}, {d.Attempts}, {d.NextAttemptAt})
            ON CONFLICT (webhook_id) DO NOTHING
            """, ct);
    }

    public async Task<ProjectionResult> UpsertOrderIfNewerAsync(CascadeDbContext db, OrderSnapshot s, DateTimeOffset now, string source, CancellationToken ct)
    {
        // xmax = 0 on the returned row means it was inserted rather than updated.
        var rows = await db.Database.SqlQuery<bool>($"""
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

        return ProjectionResults.From(rows);
    }
}

public sealed class SqlServerDialect : IDatabaseDialect
{
    // 2627: violation of a PRIMARY KEY or UNIQUE constraint. 2601: duplicate key in a unique index.
    private static bool IsDuplicateKey(Exception? ex) => ex is SqlException { Number: 2627 or 2601 };

    public bool IsUniqueViolation(DbUpdateException ex) => IsDuplicateKey(ex.InnerException);

    public IQueryable<WebhookDelivery> ClaimNextDelivery(CascadeDbContext db, DateTimeOffset now)
    {
        // UPDLOCK holds the row until the transaction ends; READPAST skips rows another worker holds (SKIP LOCKED);
        // ROWLOCK stops SQL Server escalating to a page lock that would block the other workers.
        var pending = nameof(DeliveryStatus.Pending);
        return db.WebhookDeliveries.FromSql($"""
            SELECT TOP (1) * FROM webhook_deliveries WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE status = {pending} AND next_attempt_at <= {now}
            ORDER BY CASE WHEN triggered_at IS NULL THEN 1 ELSE 0 END, triggered_at, received_at
            """);
    }

    public async Task<int> InsertDeliveryIfNewAsync(CascadeDbContext db, WebhookDelivery d, CancellationToken ct)
    {
        var status = d.Status.ToString();
        try
        {
            // UPDLOCK + HOLDLOCK lock the key range being checked, so two concurrent retries of the same delivery
            // serialise here instead of both passing NOT EXISTS (SQL Server has no ON CONFLICT DO NOTHING).
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO webhook_deliveries (webhook_id, event_id, topic, shop_domain, api_version, triggered_at, received_at, payload, status, attempts, next_attempt_at)
                SELECT {d.WebhookId}, {d.EventId}, {d.Topic}, {d.ShopDomain}, {d.ApiVersion}, {d.TriggeredAt}, {d.ReceivedAt}, {d.Payload}, {status}, {d.Attempts}, {d.NextAttemptAt}
                WHERE NOT EXISTS (SELECT 1 FROM webhook_deliveries WITH (UPDLOCK, HOLDLOCK) WHERE webhook_id = {d.WebhookId})
                """, ct);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            return 0; // belt and braces: the primary key still decides
        }
    }

    public async Task<ProjectionResult> UpsertOrderIfNewerAsync(CascadeDbContext db, OrderSnapshot s, DateTimeOffset now, string source, CancellationToken ct)
    {
        // A plain decimal parameter would default to decimal(18,2) and round the column's 4 decimal places.
        var totalPrice = new SqlParameter("@total_price", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = s.TotalPrice };

        // MERGE ... WITH (HOLDLOCK) is the SQL Server equivalent of INSERT ... ON CONFLICT DO UPDATE ... WHERE.
        // HOLDLOCK is required: without it two concurrent MERGEs can both take the NOT MATCHED branch.
        // OUTPUT $action tells inserted from updated, like RETURNING (xmax = 0); a stale snapshot outputs no row.
        var rows = await db.Database.SqlQuery<bool>($"""
            MERGE shopify_orders WITH (HOLDLOCK) AS o
            USING (VALUES ({s.ShopDomain}, {s.Id}, {s.Name}, {s.FinancialStatus}, {s.FulfillmentStatus}, {s.Cancelled}, {totalPrice}, {s.Currency}, {s.UpdatedAt}, {now}, {source}))
                AS src (shop_domain, id, name, financial_status, fulfillment_status, cancelled, total_price, currency, shopify_updated_at, last_synced_at, last_source)
            ON o.shop_domain = src.shop_domain AND o.id = src.id
            WHEN MATCHED AND o.shopify_updated_at < src.shopify_updated_at THEN UPDATE SET
                name = src.name,
                financial_status = src.financial_status,
                fulfillment_status = src.fulfillment_status,
                cancelled = src.cancelled,
                total_price = src.total_price,
                currency = src.currency,
                shopify_updated_at = src.shopify_updated_at,
                last_synced_at = src.last_synced_at,
                last_source = src.last_source
            WHEN NOT MATCHED THEN
                INSERT (shop_domain, id, name, financial_status, fulfillment_status, cancelled, total_price, currency, shopify_updated_at, last_synced_at, last_source)
                VALUES (src.shop_domain, src.id, src.name, src.financial_status, src.fulfillment_status, src.cancelled, src.total_price, src.currency, src.shopify_updated_at, src.last_synced_at, src.last_source)
            OUTPUT CAST(CASE WHEN $action = 'INSERT' THEN 1 ELSE 0 END AS bit) AS [Value];
            """).ToListAsync(ct);

        return ProjectionResults.From(rows);
    }
}

internal static class ProjectionResults
{
    /// <summary>No row back means the stored order was already as new or newer.</summary>
    public static ProjectionResult From(List<bool> insertedFlags) =>
        insertedFlags.Count == 0 ? ProjectionResult.Stale : insertedFlags[0] ? ProjectionResult.Created : ProjectionResult.Updated;
}
