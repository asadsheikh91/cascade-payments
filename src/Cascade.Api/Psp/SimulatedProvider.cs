using System.Collections.Concurrent;
using Cascade.Api.Payments;

namespace Cascade.Api.Psp;

/// <summary>
/// Deterministic in-memory PSP so the stack runs without sandbox keys, and so tests can force every outcome.
/// The payment method selects the behaviour:
///   sim_ok (or anything unrecognised)  approve
///   sim_soft_decline                   soft decline (issuer_not_available)
///   sim_hard_decline                   hard decline (stolen_card)
///   sim_insufficient_funds             hard decline (insufficient_funds)
///   sim_timeout                        charge IS captured, but the response is lost (the dangerous case)
///   sim_timeout_lost                   the request never reached the PSP; no charge exists
/// </summary>
public sealed class SimulatedProvider(string name = "simulated") : IPaymentProvider
{
    private readonly ConcurrentDictionary<Guid, (string Reference, long Amount, long Refunded)> _charges = new();

    public string Name { get; } = name;

    public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
    {
        // Idempotent on attempt id, exactly like a real PSP idempotency key.
        if (_charges.TryGetValue(request.AttemptId, out var existing))
            return Task.FromResult(ChargeResult.Approved(existing.Reference));

        switch (request.PaymentMethod)
        {
            case "sim_soft_decline":
                return Task.FromResult(ChargeResult.Declined("issuer_not_available"));
            case "sim_hard_decline":
                return Task.FromResult(ChargeResult.Declined("stolen_card"));
            case "sim_insufficient_funds":
                return Task.FromResult(ChargeResult.Declined("insufficient_funds"));
            case "sim_timeout":
                Capture(request);
                return Task.FromResult(ChargeResult.Unknown("psp_timeout"));
            case "sim_timeout_lost":
                return Task.FromResult(ChargeResult.Unknown("psp_timeout"));
            default:
                return Task.FromResult(ChargeResult.Approved(Capture(request)));
        }
    }

    public Task<LookupResult> LookupAsync(Guid attemptId, CancellationToken ct) =>
        Task.FromResult(_charges.TryGetValue(attemptId, out var c)
            ? new LookupResult(LookupStatus.Approved, c.Reference)
            : new LookupResult(LookupStatus.NotFound));

    public Task<RefundResult> RefundAsync(Guid refundId, string pspReference, long amount, CancellationToken ct)
    {
        var match = _charges.FirstOrDefault(kv => kv.Value.Reference == pspReference);
        if (match.Value.Reference is null) return Task.FromResult(new RefundResult(false, null, "charge_not_found"));
        if (match.Value.Refunded + amount > match.Value.Amount) return Task.FromResult(new RefundResult(false, null, "amount_too_large"));

        _charges[match.Key] = match.Value with { Refunded = match.Value.Refunded + amount };
        return Task.FromResult(new RefundResult(true, $"{Name}_re_{refundId:N}"));
    }

    private string Capture(ChargeRequest request)
    {
        var reference = $"{Name}_ch_{request.AttemptId:N}";
        _charges[request.AttemptId] = (reference, request.Amount, 0);
        return reference;
    }
}
