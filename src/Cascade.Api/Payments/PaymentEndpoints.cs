using Cascade.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Payments;

public sealed record CreatePaymentRequest(long Amount, string Currency, string PaymentMethod, string? MerchantReference);
public sealed record CreateRefundRequest(long Amount);

public sealed record AttemptDto(int Sequence, string Psp, AttemptOutcome Outcome, string? PspReference, string? DeclineCode, DateTimeOffset CreatedAt);
public sealed record RefundDto(Guid Id, long Amount, RefundStatus Status, string? PspReference, DateTimeOffset CreatedAt);
public sealed record PaymentDto(
    Guid Id, string MerchantReference, long Amount, string Currency, long RefundedAmount, PaymentStatus Status,
    string? Psp, string? PspReference, string? DeclineCode, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<AttemptDto> Attempts, IReadOnlyList<RefundDto> Refunds)
{
    public static PaymentDto From(Payment p) => new(
        p.Id, p.MerchantReference, p.Amount, p.Currency, p.RefundedAmount, p.Status, p.Psp, p.PspReference, p.DeclineCode, p.CreatedAt, p.UpdatedAt,
        p.Attempts.OrderBy(a => a.Sequence).Select(a => new AttemptDto(a.Sequence, a.Psp, a.Outcome, a.PspReference, a.DeclineCode, a.CreatedAt)).ToList(),
        p.Refunds.OrderBy(r => r.CreatedAt).Select(r => new RefundDto(r.Id, r.Amount, r.Status, r.PspReference, r.CreatedAt)).ToList());
}

public static class PaymentEndpoints
{
    public static void MapPayments(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/payments");

        g.MapPost("/", async (CreatePaymentRequest body, HttpRequest http, PaymentService payments, CancellationToken ct) =>
        {
            string? key = http.Headers["Idempotency-Key"];
            if (string.IsNullOrWhiteSpace(key) || key.Length > 255)
                return Results.Problem("Idempotency-Key header is required (max 255 chars).", statusCode: 400);
            if (body.Amount <= 0 || body.Currency is not { Length: 3 } || string.IsNullOrWhiteSpace(body.PaymentMethod))
                return Results.Problem("amount (minor units, > 0), currency (ISO 4217) and paymentMethod are required.", statusCode: 400);

            var (payment, created) = await payments.CreateAsync(
                new CreatePaymentCommand(key, body.MerchantReference ?? "", body.Amount, body.Currency, body.PaymentMethod), ct);

            // A replay must describe the same request; reusing a key for a different payment is a client bug.
            if (!created && (payment.Amount != body.Amount || !string.Equals(payment.Currency, body.Currency, StringComparison.OrdinalIgnoreCase)))
                return Results.Problem("Idempotency-Key was already used with different parameters.", statusCode: 422);

            return created
                ? Results.Created($"/payments/{payment.Id}", PaymentDto.From(payment))
                : Results.Ok(PaymentDto.From(payment));
        });

        g.MapGet("/{id:guid}", async (Guid id, CascadeDbContext db, CancellationToken ct) =>
            await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } p
                ? Results.Ok(PaymentDto.From(p))
                : Results.NotFound());

        g.MapGet("/", async (CascadeDbContext db, int? limit, PaymentStatus? status, CancellationToken ct) =>
        {
            var q = db.Payments.AsNoTracking().AsSplitQuery();
            if (status is not null) q = q.Where(p => p.Status == status);
            var rows = await q.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
            return Results.Ok(rows.Select(PaymentDto.From));
        });

        g.MapPost("/{id:guid}/verify", async (Guid id, PaymentService payments, CancellationToken ct) =>
            await payments.VerifyAsync(id, ct) is { } p ? Results.Ok(PaymentDto.From(p)) : Results.NotFound());

        g.MapPost("/{id:guid}/refunds", async (Guid id, CreateRefundRequest body, PaymentService payments, CancellationToken ct) =>
        {
            var (payment, refund, error) = await payments.RefundAsync(id, body.Amount, ct);
            if (payment is null) return Results.NotFound();
            if (error is not null) return Results.Problem($"PSP rejected the refund: {error}", statusCode: 502);
            return Results.Created($"/payments/{id}", PaymentDto.From(payment));
        });
    }
}
