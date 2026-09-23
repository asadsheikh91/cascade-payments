# ADR 0001: Which header deduplicates Shopify webhooks

- Status: Accepted
- Date: 2026-09-24

## Context

Shopify delivers webhooks at least once. Deliveries are retried (8 times over 4 hours) whenever we fail to return 2xx within 5 seconds, so duplicates are normal, and we need an idempotency key.

Shopify's own pages disagree about which header to use:

- The current *Verify deliveries* page defines two headers with different scopes:
  - `X-Shopify-Webhook-Id` identifies **a delivery**. Its guidance is to use it to "deduplicate individual deliveries". A retry of the same delivery carries the same value.
  - `X-Shopify-Event-Id` identifies **the merchant action** that caused the webhook. It is shared by every delivery of that action, including deliveries to *different subscriptions*. Its guidance is to use it to "correlate deliveries that originated from the same merchant action".
- The older *Best practices* tutorial only mentions `X-Shopify-Webhook-Id` for ignoring duplicates. Some community material recommends `X-Shopify-Event-Id` for the same job.

The two keys differ when one topic has two subscriptions. That happens when an app is subscribed in both `shopify.app.toml` and through the Admin API, during a subscription migration, or with two endpoints. One order payment then produces two deliveries with **different webhook ids and the same event id**. If we deduplicate only on the webhook id, we process the payment twice. If we deduplicate only on the event id, we lose the per-delivery audit trail. And if Shopify ever omits the event id (older API versions, other topics), we have no key at all.

## Decision

We use both, at different layers:

1. **Inbox (delivery level): `X-Shopify-Webhook-Id` is the primary key of `webhook_deliveries`.** The receiver does `INSERT … ON CONFLICT (webhook_id) DO NOTHING` and returns 200 either way. A retry costs one index lookup and is never processed twice. We keep every distinct delivery, so we can see that two subscriptions exist.
2. **Processing (business level): `(shop_domain, X-Shopify-Event-Id)` is the primary key of `processed_shopify_events`.** Before applying a delivery, the processor checks this key. A second delivery of an already-applied event is marked `DuplicateEvent` and skipped. The insert into this table happens in the same transaction as the side effect, so two workers racing on the two deliveries cannot both commit. The loser rolls back and is retried, and on the retry it sees the event as done.
3. **Fallback:** if `X-Shopify-Event-Id` is missing, the event key falls back to `delivery:<webhook id>`. That is no worse than webhook-id-only dedup.
4. **The handlers are still idempotent on their own.** The order projection applies a snapshot only if its `updated_at` is newer than the one stored. Replaying any delivery, in any order, is therefore harmless even if both keys above fail. The keys are an optimisation and an audit trail. Correctness doesn't depend on them.

## Consequences

- Retries and cross-subscription duplicates are both covered, and tests exercise each (`Retried_delivery_with_same_webhook_id_is_stored_once` and `Same_event_via_two_subscriptions_is_processed_once`).
- `processed_shopify_events` grows without bound. It needs a retention job (for example, drop rows older than 30 days, well past Shopify's 4-hour retry window). This is not built yet.
- Ordering is a separate problem that dedup doesn't solve. Deliveries are drained in `X-Shopify-Triggered-At` order to reduce churn, but late arrivals still happen. The `updated_at` guard is what makes that safe. Missed deliveries are caught by the reconciliation fetch (`ShopifyReconciler`), which goes through the same guarded projection.
