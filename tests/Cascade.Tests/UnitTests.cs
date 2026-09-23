using System.Text;
using Cascade.Api.Payments;
using Cascade.Api.Psp;
using Cascade.Api.Shopify;

namespace Cascade.Tests;

public class PaymentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment(long amount = 1000) => new("key", "order-1", amount, "eur", "sim_ok", Now);

    [Fact]
    public void Approved_attempt_succeeds()
    {
        var p = NewPayment();
        var a = p.StartAttempt("simulated", Now);
        p.RecordAttemptResult(a, ChargeResult.Approved("ch_1"), Now);

        Assert.Equal(PaymentStatus.Succeeded, p.Status);
        Assert.Equal("ch_1", p.PspReference);
        Assert.Equal("EUR", p.Currency);
    }

    [Fact]
    public void Unknown_outcome_is_not_a_decline()
    {
        var p = NewPayment();
        var a = p.StartAttempt("simulated", Now);
        p.RecordAttemptResult(a, ChargeResult.Unknown("psp_timeout"), Now);

        Assert.Equal(PaymentStatus.RequiresVerification, p.Status);
        Assert.Throws<InvalidPaymentTransitionException>(() => p.StartRefund(100, Now));
    }

    [Fact]
    public void Declined_is_terminal()
    {
        var p = NewPayment();
        var a = p.StartAttempt("simulated", Now);
        p.RecordAttemptResult(a, ChargeResult.Declined("stolen_card"), Now);
        p.MarkDeclined(Now);

        Assert.Equal(PaymentStatus.Declined, p.Status);
        Assert.Throws<InvalidPaymentTransitionException>(() => p.StartAttempt("simulated", Now));
        Assert.Throws<InvalidPaymentTransitionException>(() => p.StartRefund(1, Now));
    }

    [Fact]
    public void Partial_then_full_refund()
    {
        var p = NewPayment(1000);
        p.RecordAttemptResult(p.StartAttempt("simulated", Now), ChargeResult.Approved("ch_1"), Now);

        p.CompleteRefund(p.StartRefund(300, Now), true, "re_1", Now);
        Assert.Equal(PaymentStatus.PartiallyRefunded, p.Status);

        p.CompleteRefund(p.StartRefund(700, Now), true, "re_2", Now);
        Assert.Equal(PaymentStatus.Refunded, p.Status);
        Assert.Equal(1000, p.RefundedAmount);
    }

    [Fact]
    public void Cannot_refund_more_than_remaining_including_in_flight()
    {
        var p = NewPayment(1000);
        p.RecordAttemptResult(p.StartAttempt("simulated", Now), ChargeResult.Approved("ch_1"), Now);
        p.StartRefund(600, Now); // still pending at the PSP

        Assert.Throws<ArgumentOutOfRangeException>(() => p.StartRefund(500, Now));
    }

    [Fact]
    public void Failed_refund_leaves_payment_untouched()
    {
        var p = NewPayment(1000);
        p.RecordAttemptResult(p.StartAttempt("simulated", Now), ChargeResult.Approved("ch_1"), Now);
        p.CompleteRefund(p.StartRefund(1000, Now), false, null, Now);

        Assert.Equal(PaymentStatus.Succeeded, p.Status);
        Assert.Equal(0, p.RefundedAmount);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Declined, PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Refunded, PaymentStatus.Succeeded)]
    [InlineData(PaymentStatus.Succeeded, PaymentStatus.Declined)]
    [InlineData(PaymentStatus.RequiresVerification, PaymentStatus.Refunded)]
    public void Illegal_transitions(PaymentStatus from, PaymentStatus to) => Assert.False(Payment.CanTransition(from, to));
}

public class DeclineClassifierTests
{
    [Theory]
    [InlineData("issuer_not_available", AttemptOutcome.SoftDecline)]
    [InlineData("do_not_honor", AttemptOutcome.SoftDecline)]
    [InlineData("processing_error", AttemptOutcome.SoftDecline)]
    [InlineData("stolen_card", AttemptOutcome.HardDecline)]
    [InlineData("insufficient_funds", AttemptOutcome.HardDecline)]
    [InlineData("expired_card", AttemptOutcome.HardDecline)]
    [InlineData("some_code_we_have_never_seen", AttemptOutcome.HardDecline)]
    public void Classifies(string code, AttemptOutcome expected) => Assert.Equal(expected, ChargeResult.Declined(code).Outcome);
}

public class ShopifyHmacTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":820982911946154508,"total_price":"10.00"}""");

    [Fact]
    public void Accepts_valid_signature() =>
        Assert.True(ShopifyHmac.IsValid(Body, ShopifyHmac.Sign(Body, "s3cret"), ["s3cret"]));

    [Fact]
    public void Rejects_tampered_body()
    {
        var sig = ShopifyHmac.Sign(Body, "s3cret");
        var tampered = Encoding.UTF8.GetBytes("""{"id":820982911946154508,"total_price":"0.01"}""");
        Assert.False(ShopifyHmac.IsValid(tampered, sig, ["s3cret"]));
    }

    [Fact]
    public void Rejects_reserialised_json_even_if_semantically_equal()
    {
        // Why verification must use the raw bytes: same JSON, different whitespace, different signature.
        var sig = ShopifyHmac.Sign(Body, "s3cret");
        var pretty = Encoding.UTF8.GetBytes("""{ "id": 820982911946154508, "total_price": "10.00" }""");
        Assert.False(ShopifyHmac.IsValid(pretty, sig, ["s3cret"]));
    }

    [Fact]
    public void Accepts_either_secret_during_rotation() =>
        Assert.True(ShopifyHmac.IsValid(Body, ShopifyHmac.Sign(Body, "old"), ["new", "old"]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void Rejects_missing_or_malformed_header(string? header) =>
        Assert.False(ShopifyHmac.IsValid(Body, header, ["s3cret"]));
}
