using Cascade.Api.Data;
using Cascade.Api.Psp;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cascade.Api.Payments;

public sealed class PaymentOptions
{
    /// <summary>Which registered provider takes new payments. Week 2 turns this into an ordered route.</summary>
    public string PrimaryPsp { get; set; } = "simulated";
    /// <summary>
    /// How long a PSP lookup must keep saying "not found" before we believe it. Stripe Search is eventually consistent,
    /// so an early NotFound after a timeout proves nothing.
    /// </summary>
    public TimeSpan NotFoundSettleWindow { get; set; } = TimeSpan.FromMinutes(2);
}

public sealed record CreatePaymentCommand(string IdempotencyKey, string MerchantReference, long Amount, string Currency, string PaymentMethod);

public sealed class PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
{
    private readonly Dictionary<string, IPaymentProvider> _byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IPaymentProvider Get(string name) =>
        _byName.TryGetValue(name, out var p) ? p : throw new InvalidOperationException($"PSP '{name}' is not registered.");

    public IReadOnlyCollection<string> Names => _byName.Keys;
}

public sealed class PaymentService(CascadeDbContext db, PaymentProviderRegistry psps, PaymentOptions options, TimeProvider clock, ILogger<PaymentService> log)
{
    public async Task<(Payment Payment, bool Created)> CreateAsync(CreatePaymentCommand cmd, CancellationToken ct)
    {
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.IdempotencyKey == cmd.IdempotencyKey, ct);
        if (existing is not null) return (existing, false);

        var provider = psps.Get(options.PrimaryPsp);
        var now = clock.GetUtcNow();
        var payment = new Payment(cmd.IdempotencyKey, cmd.MerchantReference, cmd.Amount, cmd.Currency, cmd.PaymentMethod, now);
        var attempt = payment.StartAttempt(provider.Name, now);
        db.Payments.Add(payment);

        // Commit the attempt BEFORE calling the PSP. If we crash mid-call, the row (and its attempt id, which is the
        // PSP idempotency key) is what lets us find out later whether money moved.
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race with a concurrent request carrying the same Idempotency-Key.
            db.ChangeTracker.Clear();
            return (await db.Payments.SingleAsync(p => p.IdempotencyKey == cmd.IdempotencyKey, ct), false);
        }

        var result = await provider.ChargeAsync(
            new ChargeRequest(payment.Id, attempt.Id, payment.Amount, payment.Currency, payment.PaymentMethod),
            // The client hanging up must not leave the attempt unrecorded.
            CancellationToken.None);

        payment.RecordAttemptResult(attempt, result, clock.GetUtcNow());
        if (result.Outcome is AttemptOutcome.SoftDecline or AttemptOutcome.HardDecline)
            payment.MarkDeclined(clock.GetUtcNow());

        await db.SaveChangesAsync(CancellationToken.None);
        log.LogInformation("Payment {PaymentId} via {Psp}: {Outcome} -> {Status}", payment.Id, provider.Name, result.Outcome, payment.Status);
        return (payment, true);
    }

    /// <summary>Resolve a RequiresVerification payment by asking the PSP about the ambiguous attempt.</summary>
    public async Task<Payment?> VerifyAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.RequiresVerification) return payment;

        var attempt = payment.Attempts.Where(a => a.Outcome == AttemptOutcome.Unknown).OrderByDescending(a => a.Sequence).First();
        var lookup = await psps.Get(attempt.Psp).LookupAsync(attempt.Id, ct);
        var now = clock.GetUtcNow();

        switch (lookup.Status)
        {
            case LookupStatus.Approved:
                payment.ResolveVerification(attempt, ChargeResult.Approved(lookup.PspReference!), now);
                break;
            case LookupStatus.Declined:
                payment.ResolveVerification(attempt, ChargeResult.Declined(lookup.DeclineCode ?? "generic_decline", lookup.PspReference), now);
                break;
            case LookupStatus.NotFound when now - attempt.CreatedAt >= options.NotFoundSettleWindow:
                payment.ResolveVerification(attempt, new ChargeResult(AttemptOutcome.HardDecline, DeclineCode: "psp_no_record"), now);
                break;
            default:
                return payment; // still ambiguous; try again later
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Payment {PaymentId} verified: {Lookup} -> {Status}", payment.Id, lookup.Status, payment.Status);
        return payment;
    }

    public async Task<(Payment? Payment, Refund? Refund, string? Error)> RefundAsync(Guid paymentId, long amount, CancellationToken ct)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return (null, null, null);

        var refund = payment.StartRefund(amount, clock.GetUtcNow());
        await db.SaveChangesAsync(ct); // xmin concurrency check: two refunds racing on one payment cannot both pass

        var result = await psps.Get(payment.Psp!).RefundAsync(refund.Id, payment.PspReference!, amount, CancellationToken.None);
        payment.CompleteRefund(refund, result.Succeeded, result.PspReference, clock.GetUtcNow());
        await db.SaveChangesAsync(CancellationToken.None);
        return (payment, refund, result.Succeeded ? null : result.FailureReason);
    }
}
