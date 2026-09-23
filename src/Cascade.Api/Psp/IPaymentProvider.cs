using Cascade.Api.Payments;

namespace Cascade.Api.Psp;

public sealed record ChargeRequest(Guid PaymentId, Guid AttemptId, long Amount, string Currency, string PaymentMethod);

/// <summary>
/// Outcome of a charge as the PSP reported it. <see cref="AttemptOutcome.Unknown"/> means "we did not get an answer";
/// it is NOT a decline, because the charge may have gone through.
/// </summary>
public sealed record ChargeResult(AttemptOutcome Outcome, string? PspReference = null, string? DeclineCode = null)
{
    public static ChargeResult Approved(string pspReference) => new(AttemptOutcome.Approved, pspReference);
    public static ChargeResult Unknown(string reason) => new(AttemptOutcome.Unknown, DeclineCode: reason);
    public static ChargeResult Declined(string declineCode, string? pspReference = null) =>
        new(DeclineClassifier.IsSoft(declineCode) ? AttemptOutcome.SoftDecline : AttemptOutcome.HardDecline, pspReference, declineCode);
}

public enum LookupStatus { Approved, Declined, NotFound, Unknown }

public sealed record LookupResult(LookupStatus Status, string? PspReference = null, string? DeclineCode = null);

public sealed record RefundResult(bool Succeeded, string? PspReference, string? FailureReason = null);

public interface IPaymentProvider
{
    string Name { get; }

    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);

    /// <summary>
    /// Asks the PSP what happened to an attempt, keyed by our attempt id. Used after a timeout, before
    /// declaring the payment failed or trying another route.
    /// </summary>
    Task<LookupResult> LookupAsync(Guid attemptId, CancellationToken ct);

    Task<RefundResult> RefundAsync(Guid refundId, string pspReference, long amount, CancellationToken ct);
}

/// <summary>
/// Soft = another route might approve it (issuer or acquirer trouble, generic risk decline).
/// Hard = the card/cardholder is the problem, and another acquirer will reach the same issuer with the same answer.
/// Unrecognised codes default to hard: a wrong "soft" costs a second authorization on a card that was never going
/// to work (and adds to the retry counts card schemes watch); a wrong "hard" only loses one recoverable sale.
/// </summary>
public static class DeclineClassifier
{
    private static readonly HashSet<string> Soft = new(StringComparer.OrdinalIgnoreCase)
    {
        "generic_decline",
        "do_not_honor",
        "processing_error",
        "issuer_not_available",
        "try_again_later",
        "reenter_transaction",
        "call_issuer",
        "approve_with_id",
        "no_action_taken",
        "service_not_allowed",
        "transaction_not_allowed",
        "card_not_supported",
        "currency_not_supported",
    };

    // insufficient_funds is deliberately hard: routing to another acquirer reaches the same issuer and the same balance.
    public static bool IsSoft(string? declineCode) => declineCode is not null && Soft.Contains(declineCode);
}
