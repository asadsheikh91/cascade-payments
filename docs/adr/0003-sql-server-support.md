# ADR 0003: SQL Server as a second database

- Status: Accepted
- Date: 2026-11-16

## Context

Cascade was built on Postgres, and its correctness guarantees lean on Postgres features: `xmin` for optimistic concurrency, `INSERT … ON CONFLICT` for the webhook inbox and the order projection, `FOR UPDATE SKIP LOCKED` for claiming inbox rows, and `jsonb` to reject invalid payloads. Many .NET shops run SQL Server, so the same guarantees should hold there, not just the same tables.

## Decision

- **Provider by configuration.** `Database:Provider` is `Postgres` (default) or `SqlServer`. `AddCascadeDatabase` registers the context and the matching `IDatabaseDialect`.
- **A context per provider.** `SqlServerCascadeDbContext` derives from `CascadeDbContext` and only overrides what differs. Each context has its own migrations (`Data/Migrations` and `Data/Migrations/SqlServer`), because migrations hold provider-specific column types. The Postgres model and migrations are unchanged.
- **One small dialect interface.** The four statements EF Core can't express portably sit behind `IDatabaseDialect`. Everything else is LINQ and runs unchanged on both databases.

| Guarantee | Postgres | SQL Server |
|---|---|---|
| Optimistic concurrency on payments | `xmin` system column | `rowversion` shadow column (`row_version`) |
| Store a webhook delivery once | `INSERT … ON CONFLICT (webhook_id) DO NOTHING` | `INSERT … SELECT … WHERE NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))`, and a duplicate-key error (2627 / 2601) is treated as "already stored" |
| Apply an order only if newer | `INSERT … ON CONFLICT DO UPDATE … WHERE older`, `RETURNING (xmax = 0)` | `MERGE … WITH (HOLDLOCK)`, `WHEN MATCHED AND older`, `OUTPUT $action` |
| Claim the next inbox row across workers | `FOR UPDATE SKIP LOCKED` | `WITH (UPDLOCK, READPAST, ROWLOCK)` |
| Reject invalid JSON payloads | `jsonb` column | `nvarchar(max)` with `CHECK (ISJSON(payload) = 1)` |
| Detect a duplicate Idempotency-Key | SQLSTATE `23505` | error 2627 / 2601 |

## Consequences

- `MERGE` needs `HOLDLOCK`. Without it, two concurrent `MERGE`s for a new order can both take the `NOT MATCHED` branch.
- A raw-SQL `decimal` parameter defaults to `decimal(18,2)` on SQL Server, so the order total is passed as an explicit `decimal(18,4)` parameter to match the column.
- CI runs every test on both databases (Testcontainers, `CASCADE_TEST_DB=sqlserver`), checks that both migrations match their models, and smoke-tests both Docker Compose stacks.
- A schema change now needs two migrations, one per context.
