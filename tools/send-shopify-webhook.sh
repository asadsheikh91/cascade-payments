#!/usr/bin/env bash
# Sends a correctly signed Shopify-style orders/* webhook to a local Cascade, for demos without a dev store.
# With a real dev store, use `shopify app webhook trigger` or place a test order instead.
#
#   tools/send-shopify-webhook.sh <order_id> <financial_status> [webhook_id] [event_id] [topic]
#
# Re-running with the same webhook_id demonstrates retry dedup; same event_id with a new webhook_id
# demonstrates the two-subscriptions case (ADR 0001).
set -euo pipefail

URL="${CASCADE_URL:-http://localhost:8080}/webhooks/shopify"
SECRET="${SHOPIFY_WEBHOOK_SECRET:-local-dev-secret}"
SHOP="${SHOPIFY_SHOP_DOMAIN:-cascade-dev.myshopify.com}"

ORDER_ID="${1:?order id}"
STATUS="${2:?financial status, e.g. pending|paid|refunded}"
WEBHOOK_ID="${3:-$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)}"
EVENT_ID="${4:-$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen)}"
TOPIC="${5:-orders/updated}"
NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

BODY=$(printf '{"id":%s,"name":"#%s","financial_status":"%s","fulfillment_status":null,"cancelled_at":null,"total_price":"49.90","currency":"EUR","updated_at":"%s"}' \
  "$ORDER_ID" "$ORDER_ID" "$STATUS" "$NOW")
HMAC=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$SECRET" -binary | base64)

curl -sS -o /dev/null -w "%{http_code}\n" -X POST "$URL" \
  -H "Content-Type: application/json" \
  -H "X-Shopify-Hmac-SHA256: $HMAC" \
  -H "X-Shopify-Topic: $TOPIC" \
  -H "X-Shopify-Shop-Domain: $SHOP" \
  -H "X-Shopify-Webhook-Id: $WEBHOOK_ID" \
  -H "X-Shopify-Event-Id: $EVENT_ID" \
  -H "X-Shopify-Triggered-At: $NOW" \
  -H "X-Shopify-API-Version: 2026-07" \
  --data-binary "$BODY"
