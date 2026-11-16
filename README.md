# Cascade

A small copy of a real payments migration: a legacy Laravel payments backend moved to .NET, with evidence that the new system behaves like the old one.

It covers four concrete problems:

1. **Migrating from Laravel to .NET while the old platform keeps serving merchants.** YARP runs in shadow mode and records every difference between the two systems. *(Week 3)*
2. **Shopify and PSP webhooks**: signed, deduplicated, out of order, sometimes missing. **(Done)**
3. **Cascading declines**: "a payment declined on one route can still find another," without double-charging on a timeout. *(Week 2. The timeout half is done.)*
4. **Payouts as reported by the acquirer**: a double-entry ledger reconciled against PSP settlement reports. *(Week 2)*

```bash
docker compose up --build
```

That brings up Postgres 17 and the API on `http://localhost:8080`. It needs no keys, because a deterministic simulated PSP is always available.

The same stack runs on **SQL Server 2022**:

```bash
docker compose -f docker-compose.sqlserver.yml up --build
```

## Status

| Phase | Scope | State |
|---|---|---|
| Week 1 | .NET 10 service, payment state machine, PSP interface + Stripe test mode, Shopify webhook path (HMAC, dedup, inbox, ordering, reconciliation fetch), xUnit + Testcontainers, Docker, GitHub Actions | **Done**: 45 tests passing |
| Week 2 | Second PSP (Adyen or Mollie sandbox), cascade routing, append-only double-entry ledger with a sum-to-zero DB constraint, settlement reconciliation, Toxiproxy fault tests | Next |
| Weeks 3–4 | React + TS dashboard (2 roles), Laravel stub, YARP shadow mode + parity report, Locust p95 | Later |

## Try it

```bash
# Approve (PaymentMethod picks the simulator's behaviour; see src/Cascade.Api/Psp/SimulatedProvider.cs)
curl -X POST localhost:8080/payments -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-1' \
  -d '{"amount":1299,"currency":"EUR","paymentMethod":"sim_ok"}'

# The dangerous case: the PSP charged the card but the response was lost
curl -X POST localhost:8080/payments -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-2' \
  -d '{"amount":500,"currency":"EUR","paymentMethod":"sim_timeout"}'      # -> RequiresVerification
curl -X POST localhost:8080/payments/<id>/verify                          # -> Succeeded, not Declined

# Refund part of it
curl -X POST localhost:8080/payments/<id>/refunds -H 'Content-Type: application/json' -d '{"amount":200}'

# Shopify: signed webhook, then a retry of the same delivery, then the same event via a second subscription
tools/send-shopify-webhook.sh 1001 paid wh-a ev-1   # 200, processed
tools/send-shopify-webhook.sh 1001 paid wh-a ev-1   # 200, dropped at the inbox (same webhook id)
tools/send-shopify-webhook.sh 1001 paid wh-b ev-1   # 200, stored, then skipped as DuplicateEvent
SHOPIFY_WEBHOOK_SECRET=wrong tools/send-shopify-webhook.sh 1001 paid   # 401
```

## Design decisions worth reading

- **[ADR 0001](docs/adr/0001-shopify-webhook-dedup-key.md): the Shopify dedup key.** Shopify's docs disagree on `X-Shopify-Webhook-Id` vs `X-Shopify-Event-Id`. Cascade uses the webhook id for the delivery inbox and the event id for processing idempotency, because two subscriptions on one topic produce two webhook ids for a single event.
- **[ADR 0003](docs/adr/0003-sql-server-support.md): SQL Server as a second database.** `Database:Provider` picks Postgres or SQL Server. Each has its own migrations, and the four statements EF Core can't express portably sit behind `IDatabaseDialect` (`ON CONFLICT` becomes `MERGE … WITH (HOLDLOCK)`, `SKIP LOCKED` becomes `UPDLOCK, READPAST`, `xmin` becomes `rowversion`). Every test runs on both databases in CI.
- **[ADR 0002](docs/adr/0002-psp-timeout-is-not-a-decline.md): a PSP timeout is not a decline.** The charge may have succeeded. The attempt is committed before the call, and its id is the PSP idempotency key. A timeout moves the payment to `RequiresVerification`, and the first PSP is asked what happened before anything else is tried.

### Payments

- **State machine** ([Payment.cs](src/Cascade.Api/Payments/Payment.cs)):
  - `Pending → Authorizing → Succeeded | Declined | RequiresVerification`
  - `RequiresVerification → Succeeded | Declined`
  - `Succeeded → PartiallyRefunded → Refunded`
  - Every other transition throws, and the API returns it as 409.
- **Money** is stored as `long` minor units. The database adds `CHECK` constraints on top of the domain rules (for example, `refunded_amount <= amount`).
- **Idempotency.** `Idempotency-Key` is required on `POST /payments` and backed by a unique index. Concurrent duplicates fall back to returning the existing row, which is tested with 8 parallel requests. Reusing a key with a different amount returns 422.
- **Concurrency.** Postgres `xmin` provides optimistic concurrency, so two refunds racing on one payment can't both pass the balance check.
- **Decline classes** ([IPaymentProvider.cs](src/Cascade.Api/Psp/IPaymentProvider.cs)). A soft decline means another route might approve. `insufficient_funds` is deliberately hard: a second acquirer reaches the same issuer and the same balance. Unknown codes default to hard, because a wrong "soft" costs a pointless second authorization.
- **Stripe** ([StripeProvider.cs](src/Cascade.Api/Psp/StripeProvider.cs)):
  - Refuses to start with a non-test key.
  - Confirms server-side with redirects disabled.
  - SDK network retries are off, so retry decisions stay in our state machine.
  - Lookup uses Search on `metadata['cascade_attempt_id']`. Search is eventually consistent, so an early "not found" doesn't count until `NotFoundSettleWindow` passes.

### Shopify

- **Receive** ([ShopifyWebhooks.cs](src/Cascade.Api/Shopify/ShopifyWebhooks.cs)). The HMAC-SHA256 is computed over the **raw body bytes** and compared in constant time. Several secrets are accepted so they can be rotated. After that, the handler does one `INSERT … ON CONFLICT DO NOTHING` and returns 200, well inside Shopify's 5-second limit.
- **Process** ([ShopifyInboxProcessor.cs](src/Cascade.Api/Shopify/ShopifyInboxProcessor.cs)):
  - Deliveries are claimed one at a time with `FOR UPDATE SKIP LOCKED`, which is safe across replicas, in `X-Shopify-Triggered-At` order.
  - A failure gets exponential backoff and is dead-lettered after 10 attempts. It is never dropped.
  - Strictly, this is a transactional *inbox* (for inbound messages) rather than an outbox. The mechanics are the same.
- **Ordering** ([ShopifyOrderProjector.cs](src/Cascade.Api/Shopify/ShopifyOrderProjector.cs)). Sorting by triggered-at isn't enough, because late deliveries still happen. Correctness comes from a single-statement upsert that applies a change only when `updated_at` is newer.
- **Reconciliation fetch** ([ShopifyReconciler.cs](src/Cascade.Api/Shopify/ShopifyReconciler.cs)). On a schedule, the GraphQL Admin API is queried for orders updated since the cursor, minus an overlap. Results go through the same guarded projection. Every order that changes is counted as "repaired", meaning an event the webhooks missed.

## Configuration

Put these in a `.env` file next to `docker-compose.yml` (it is gitignored):

| Variable | Effect |
|---|---|
| `STRIPE_SECRET_KEY=sk_test_…` | Registers the Stripe adapter (test keys only) |
| `PSP_PRIMARY=stripe` | Routes new payments to Stripe. Payment methods are then Stripe test ids such as `pm_card_visa` or `pm_card_chargeDeclinedInsufficientFunds` |
| `SHOPIFY_WEBHOOK_SECRET` | The app's client secret, used to verify webhook HMACs |
| `SHOPIFY_SHOP_DOMAIN`, `SHOPIFY_ADMIN_TOKEN` | Enable the reconciliation job against a dev store |

To receive real webhooks from a Shopify dev store, expose port 8080 (for example with `cloudflared tunnel --url http://localhost:8080`). Subscribe `orders/create`, `orders/updated` and `orders/paid` to `<tunnel>/webhooks/shopify`.

## Development

```bash
dotnet test                              # needs Docker: Testcontainers starts postgres:17-alpine
CASCADE_TEST_DB=sqlserver dotnet test    # the same tests against SQL Server 2022

# A schema change needs one migration per provider (after `dotnet tool restore`):
dotnet ef migrations add <Name> -p src/Cascade.Api --context CascadeDbContext -o Data/Migrations
dotnet ef migrations add <Name> -p src/Cascade.Api --context SqlServerCascadeDbContext -o Data/Migrations/SqlServer --namespace Cascade.Api.Data.Migrations.SqlServer
```

## Known gaps (deliberate, for now)

- Verification is triggered by a manual `POST /verify`. A sweeper job for stuck `RequiresVerification` payments comes with cascade routing.
- A refund whose PSP call times out stays `Pending` and blocks that amount. It needs the same Unknown handling that charges have.
- `processed_shopify_events` has no retention job yet.
- The simulated PSP keeps its state in memory, so restarting the API forgets simulated charges.
- There is no authentication yet. The two roles arrive with the dashboard.
