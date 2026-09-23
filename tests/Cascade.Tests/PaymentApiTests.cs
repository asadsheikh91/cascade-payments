using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.Api.Payments;

namespace Cascade.Tests;

[Collection(DbCollection.Name)]
public class PaymentApiTests(CascadeFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http = factory.CreateClient();

    private async Task<(HttpStatusCode Status, PaymentDto? Body)> CreateAsync(string method, long amount = 2500, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new { amount, currency = "EUR", paymentMethod = method, merchantReference = "order-42" }),
        };
        req.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        var res = await _http.SendAsync(req);
        return (res.StatusCode, res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<PaymentDto>(Json) : null);
    }

    [Fact]
    public async Task Approved_payment_is_created()
    {
        var (status, p) = await CreateAsync("sim_ok");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(PaymentStatus.Succeeded, p!.Status);
        Assert.Single(p.Attempts);
        Assert.StartsWith("simulated_ch_", p.PspReference);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_payment_without_charging_again()
    {
        var key = Guid.NewGuid().ToString();
        var (s1, first) = await CreateAsync("sim_ok", key: key);
        var (s2, replay) = await CreateAsync("sim_ok", key: key);

        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal(first!.Id, replay!.Id);
        Assert.Single(replay.Attempts);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_one_payment()
    {
        var key = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateAsync("sim_ok", key: key)));

        Assert.All(results, r => Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK));
        Assert.Single(results.Select(r => r.Body!.Id).Distinct());
        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Reusing_a_key_with_different_amount_is_rejected()
    {
        var key = Guid.NewGuid().ToString();
        await CreateAsync("sim_ok", amount: 1000, key: key);
        var (status, _) = await CreateAsync("sim_ok", amount: 9999, key: key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        var res = await _http.PostAsJsonAsync("/payments", new { amount = 100, currency = "EUR", paymentMethod = "sim_ok" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("sim_hard_decline", "stolen_card", AttemptOutcome.HardDecline)]
    [InlineData("sim_soft_decline", "issuer_not_available", AttemptOutcome.SoftDecline)]
    public async Task Declines_are_recorded_with_their_class(string method, string code, AttemptOutcome outcome)
    {
        var (_, p) = await CreateAsync(method);

        Assert.Equal(PaymentStatus.Declined, p!.Status);
        Assert.Equal(code, p.DeclineCode);
        Assert.Equal(outcome, p.Attempts.Single().Outcome);
    }

    [Fact]
    public async Task Timeout_where_the_charge_went_through_resolves_to_succeeded_not_declined()
    {
        var (_, p) = await CreateAsync("sim_timeout");
        Assert.Equal(PaymentStatus.RequiresVerification, p!.Status);

        var verified = await (await _http.PostAsync($"/payments/{p.Id}/verify", null)).Content.ReadFromJsonAsync<PaymentDto>(Json);

        Assert.Equal(PaymentStatus.Succeeded, verified!.Status);
        Assert.StartsWith("simulated_ch_", verified.PspReference);
    }

    [Fact]
    public async Task Timeout_where_the_psp_has_no_record_resolves_to_declined_after_settle_window()
    {
        var (_, p) = await CreateAsync("sim_timeout_lost");
        var verified = await (await _http.PostAsync($"/payments/{p!.Id}/verify", null)).Content.ReadFromJsonAsync<PaymentDto>(Json);

        Assert.Equal(PaymentStatus.Declined, verified!.Status);
        Assert.Equal("psp_no_record", verified.DeclineCode);
    }

    [Fact]
    public async Task Partial_and_full_refunds()
    {
        var (_, p) = await CreateAsync("sim_ok", amount: 1000);

        var r1 = await _http.PostAsJsonAsync($"/payments/{p!.Id}/refunds", new { amount = 400 });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(PaymentStatus.PartiallyRefunded, (await r1.Content.ReadFromJsonAsync<PaymentDto>(Json))!.Status);

        var tooMuch = await _http.PostAsJsonAsync($"/payments/{p.Id}/refunds", new { amount = 601 });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        var r2 = await _http.PostAsJsonAsync($"/payments/{p.Id}/refunds", new { amount = 600 });
        var done = await r2.Content.ReadFromJsonAsync<PaymentDto>(Json);
        Assert.Equal(PaymentStatus.Refunded, done!.Status);
        Assert.Equal(1000, done.RefundedAmount);
        Assert.Equal(2, done.Refunds.Count);
    }

    [Fact]
    public async Task Refunding_a_declined_payment_is_a_conflict()
    {
        var (_, p) = await CreateAsync("sim_hard_decline");
        var res = await _http.PostAsJsonAsync($"/payments/{p!.Id}/refunds", new { amount = 1 });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Database_rejects_a_refunded_amount_above_the_payment_amount()
    {
        // Defence in depth: even if domain code were bypassed, the CHECK constraint holds.
        var (_, p) = await CreateAsync("sim_ok", amount: 100);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db =>
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(db.Database,
                $"UPDATE payments SET refunded_amount = 101 WHERE id = {p!.Id}")));
        Assert.Contains("ck_payments_refunded_within_amount", ex.ToString());
    }
}
