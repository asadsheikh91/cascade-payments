using Cascade.Api.Psp;
using System.ComponentModel.DataAnnotations;

namespace Cascade.Api.Payments;

public enum PaymentStatus
{
    Pending,
    Authorizing,
    Succeeded,
    Declined,
    /// <summary>The PSP did not give a definitive answer (timeout, 5xx). The charge may exist.</summary>
    RequiresVerification,
    PartiallyRefunded,
    Refunded,
}

public enum AttemptOutcome
{
    Pending,
    Approved,
    SoftDecline,
    HardDecline,
    Unknown,
}

public enum RefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

public sealed class InvalidPaymentTransitionException(PaymentStatus from, PaymentStatus to)
    : InvalidOperationException($"Payment cannot move from {from} to {to}.")
{
    public PaymentStatus From { get; } = from;
    public PaymentStatus To { get; } = to;
}

public sealed class Payment
{
    // The whole state machine lives here. Anything not listed is illegal, including self-loops,
    // except PartiallyRefunded -> PartiallyRefunded (a second partial refund).
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [PaymentStatus.Pending] = [PaymentStatus.Authorizing],
        [PaymentStatus.Authorizing] = [PaymentStatus.Succeeded, PaymentStatus.Declined, PaymentStatus.RequiresVerification],
        [PaymentStatus.RequiresVerification] = [PaymentStatus.Succeeded, PaymentStatus.Declined],
        [PaymentStatus.Succeeded] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.PartiallyRefunded] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.Declined] = [],
        [PaymentStatus.Refunded] = [],
    };

    private Payment() { }

    public Payment(string idempotencyKey, string merchantReference, long amount, string currency, string paymentMethod, DateTimeOffset now)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive (minor units).");
        if (currency is not { Length: 3 }) throw new ArgumentException("Currency must be an ISO 4217 code.", nameof(currency));

        Id = Guid.CreateVersion7(now);
        IdempotencyKey = idempotencyKey;
        MerchantReference = merchantReference;
        Amount = amount;
        Currency = currency.ToUpperInvariant();
        PaymentMethod = paymentMethod;
        Status = PaymentStatus.Pending;
        CreatedAt = UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public string MerchantReference { get; private set; } = "";
    /// <summary>Minor units (cents). Never floating point.</summary>
    public long Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string PaymentMethod { get; private set; } = "";
    public long RefundedAmount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? Psp { get; private set; }
    public string? PspReference { get; private set; }
    public string? DeclineCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Maps to Postgres xmin for optimistic concurrency.</summary>
    [Timestamp]
    public uint Version { get; private set; }

    public List<PaymentAttempt> Attempts { get; private set; } = [];
    public List<Refund> Refunds { get; private set; } = [];

    public long RefundableAmount => Amount - RefundedAmount;

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) => Allowed[from].Contains(to);

    private void TransitionTo(PaymentStatus to, DateTimeOffset now)
    {
        if (!CanTransition(Status, to)) throw new InvalidPaymentTransitionException(Status, to);
        Status = to;
        UpdatedAt = now;
    }

    public PaymentAttempt StartAttempt(string psp, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Pending) TransitionTo(PaymentStatus.Authorizing, now);
        else if (Status != PaymentStatus.Authorizing) throw new InvalidPaymentTransitionException(Status, PaymentStatus.Authorizing);

        var attempt = new PaymentAttempt(Id, Attempts.Count + 1, psp, now);
        Attempts.Add(attempt);
        return attempt;
    }

    public void RecordAttemptResult(PaymentAttempt attempt, ChargeResult result, DateTimeOffset now)
    {
        attempt.Complete(result, now);
        Psp = attempt.Psp;
        PspReference = result.PspReference ?? PspReference;
        DeclineCode = result.DeclineCode;

        switch (result.Outcome)
        {
            case AttemptOutcome.Approved:
                TransitionTo(PaymentStatus.Succeeded, now);
                break;
            case AttemptOutcome.Unknown:
                TransitionTo(PaymentStatus.RequiresVerification, now);
                break;
            case AttemptOutcome.SoftDecline:
            case AttemptOutcome.HardDecline:
                // The caller decides whether to cascade; if it does not, the payment is declined.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    public void MarkDeclined(DateTimeOffset now) => TransitionTo(PaymentStatus.Declined, now);

    /// <summary>Resolves an ambiguous attempt once the PSP has told us what actually happened.</summary>
    public void ResolveVerification(PaymentAttempt attempt, ChargeResult result, DateTimeOffset now)
    {
        if (Status != PaymentStatus.RequiresVerification)
            throw new InvalidPaymentTransitionException(Status, result.Outcome == AttemptOutcome.Approved ? PaymentStatus.Succeeded : PaymentStatus.Declined);

        attempt.Complete(result, now);
        PspReference = result.PspReference ?? PspReference;
        DeclineCode = result.DeclineCode;
        TransitionTo(result.Outcome == AttemptOutcome.Approved ? PaymentStatus.Succeeded : PaymentStatus.Declined, now);
    }

    public Refund StartRefund(long amount, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
            throw new InvalidPaymentTransitionException(Status, PaymentStatus.Refunded);
        var inFlight = Refunds.Where(r => r.Status == RefundStatus.Pending).Sum(r => r.Amount);
        if (amount <= 0 || amount > RefundableAmount - inFlight)
            throw new ArgumentOutOfRangeException(nameof(amount), $"Refund must be between 1 and {RefundableAmount - inFlight} minor units.");

        var refund = new Refund(Id, amount, now);
        Refunds.Add(refund);
        UpdatedAt = now;
        return refund;
    }

    public void CompleteRefund(Refund refund, bool succeeded, string? pspReference, DateTimeOffset now)
    {
        refund.Complete(succeeded, pspReference, now);
        if (!succeeded) return;

        RefundedAmount += refund.Amount;
        TransitionTo(RefundedAmount == Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, now);
    }
}

public sealed class PaymentAttempt
{
    private PaymentAttempt() { }

    internal PaymentAttempt(Guid paymentId, int sequence, string psp, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        PaymentId = paymentId;
        Sequence = sequence;
        Psp = psp;
        Outcome = AttemptOutcome.Pending;
        CreatedAt = now;
    }

    /// <summary>Also sent to the PSP as the idempotency key, so a retry of this attempt cannot double-charge.</summary>
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public int Sequence { get; private set; }
    public string Psp { get; private set; } = "";
    public AttemptOutcome Outcome { get; private set; }
    public string? PspReference { get; private set; }
    public string? DeclineCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    internal void Complete(ChargeResult result, DateTimeOffset now)
    {
        Outcome = result.Outcome;
        PspReference = result.PspReference ?? PspReference;
        DeclineCode = result.DeclineCode;
        CompletedAt = now;
    }
}

public sealed class Refund
{
    private Refund() { }

    internal Refund(Guid paymentId, long amount, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7(now);
        PaymentId = paymentId;
        Amount = amount;
        Status = RefundStatus.Pending;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public long Amount { get; private set; }
    public RefundStatus Status { get; private set; }
    public string? PspReference { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    internal void Complete(bool succeeded, string? pspReference, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending) throw new InvalidOperationException($"Refund {Id} is already {Status}.");
        Status = succeeded ? RefundStatus.Succeeded : RefundStatus.Failed;
        PspReference = pspReference;
        CompletedAt = now;
    }
}
