# ADR 0002: A PSP timeout is not a decline

- Status: Accepted
- Date: 2026-09-24

## Context

When a charge request to a PSP times out, returns a 5xx, or the connection drops after the request was sent, we don't know whether the card was charged. The acquirer may have approved it, and only the response was lost.

If we treat that as a decline, one of two things goes wrong. We tell the merchant the payment failed although the customer paid, or, once cascading exists (Week 2), we send the charge to the second PSP and **charge the customer twice**.

## Decision

- PSP adapters return three kinds of outcome: `Approved`, `SoftDecline`/`HardDecline`, or `Unknown`. Any ambiguous transport failure maps to `Unknown`: a timeout, a 5xx, or a network error after sending. A 4xx card error is a real decline.
- `Unknown` moves the payment to `RequiresVerification`, a state of its own. It is neither `Declined` nor `Succeeded`, and refunds are refused until it resolves.
- Every attempt row is committed **before** the PSP call, and its id is sent to the PSP as the idempotency key (Stripe `Idempotency-Key`, plus a `cascade_attempt_id` metadata field). A retry of the same attempt can't create a second charge, and we can always look the attempt up later.
- Verification calls `LookupAsync(attemptId)` on the **same** PSP:
  - `Approved` → `Succeeded`.
  - `Declined` → `Declined`.
  - `NotFound` counts only after a settle window (`Payments:NotFoundSettleWindow`, 2 minutes by default), because Stripe Search is eventually consistent. Before then the payment stays in `RequiresVerification`.
- The SDK's automatic network retries are disabled (`maxNetworkRetries: 0`). The retry decision needs our state machine, not a transport-level loop.

## Consequences

- Week 2 cascade rule: cascade only on `SoftDecline`. On `Unknown`, verify first, and cascade only if the first PSP definitively has no charge.
- `RequiresVerification` payments need a sweeper (a background job that verifies them on a schedule). Today verification is triggered through `POST /payments/{id}/verify`.
- Refund timeouts have the same ambiguity. For now a refund exception surfaces as an error. Adding a `RefundStatus.Unknown` is tracked for Week 2.
