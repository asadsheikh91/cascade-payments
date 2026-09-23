using System.Net;
using Stripe;

namespace Cascade.Api.Psp;

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    /// <summary>Our own deadline for a PSP call. Past it, the outcome is Unknown, not declined.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Overridable so fault-injection tests can route Stripe traffic through a proxy.</summary>
    public string? ApiBase { get; set; }
}

/// <summary>
/// Stripe test mode behind <see cref="IPaymentProvider"/>. Server-side confirmation with a PaymentMethod id
/// (e.g. pm_card_visa, pm_card_chargeDeclinedInsufficientFunds); redirects disabled because there is no browser.
/// </summary>
public sealed class StripeProvider : IPaymentProvider
{
    private const string AttemptMetadataKey = "cascade_attempt_id";
    private readonly PaymentIntentService _intents;
    private readonly RefundService _refunds;
    private readonly ILogger<StripeProvider> _log;

    public StripeProvider(StripeOptions options, HttpClient http, ILogger<StripeProvider> log)
    {
        if (string.IsNullOrWhiteSpace(options.SecretKey)) throw new InvalidOperationException("Stripe:SecretKey is not configured.");
        if (!options.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) && !options.SecretKey.StartsWith("rk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to start with a non-test Stripe key.");

        http.Timeout = options.Timeout;
        var client = new StripeClient(
            apiKey: options.SecretKey,
            // Retries are ours to decide: an automatic SDK retry after a timeout hides exactly the ambiguity we must handle.
            httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 0),
            apiBase: options.ApiBase);
        _intents = new PaymentIntentService(client);
        _refunds = new RefundService(client);
        _log = log;
    }

    public string Name => "stripe";

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
    {
        var create = new PaymentIntentCreateOptions
        {
            Amount = request.Amount,
            Currency = request.Currency.ToLowerInvariant(),
            PaymentMethod = request.PaymentMethod,
            Confirm = true,
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true, AllowRedirects = "never" },
            Metadata = new Dictionary<string, string>
            {
                [AttemptMetadataKey] = request.AttemptId.ToString(),
                ["cascade_payment_id"] = request.PaymentId.ToString(),
            },
        };

        try
        {
            var intent = await _intents.CreateAsync(create, new RequestOptions { IdempotencyKey = request.AttemptId.ToString() }, ct);
            return intent.Status switch
            {
                "succeeded" => ChargeResult.Approved(intent.Id),
                "processing" => ChargeResult.Unknown("psp_processing"),
                "requires_action" => ChargeResult.Declined("authentication_required", intent.Id),
                _ => ChargeResult.Declined(intent.LastPaymentError?.DeclineCode ?? intent.LastPaymentError?.Code ?? "generic_decline", intent.Id),
            };
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
        {
            return ChargeResult.Declined(ex.StripeError.DeclineCode ?? ex.StripeError.Code ?? "generic_decline", ex.StripeError.PaymentIntent?.Id);
        }
        catch (Exception ex) when (IsAmbiguous(ex, ct))
        {
            _log.LogWarning(ex, "Stripe charge for attempt {AttemptId} has an unknown outcome", request.AttemptId);
            return ChargeResult.Unknown("psp_timeout");
        }
    }

    public async Task<LookupResult> LookupAsync(Guid attemptId, CancellationToken ct)
    {
        try
        {
            // Stripe Search is eventually consistent (documented as usually under a minute), so NotFound right after a
            // timeout does not prove absence. The caller must treat early NotFound as "still unknown".
            var page = await _intents.SearchAsync(new PaymentIntentSearchOptions
            {
                Query = $"metadata['{AttemptMetadataKey}']:'{attemptId}'",
                Limit = 1,
            }, cancellationToken: ct);

            var intent = page.Data.FirstOrDefault();
            if (intent is null) return new LookupResult(LookupStatus.NotFound);
            return intent.Status switch
            {
                "succeeded" => new LookupResult(LookupStatus.Approved, intent.Id),
                "processing" => new LookupResult(LookupStatus.Unknown, intent.Id),
                _ => new LookupResult(LookupStatus.Declined, intent.Id, intent.LastPaymentError?.DeclineCode ?? intent.LastPaymentError?.Code),
            };
        }
        catch (Exception ex) when (IsAmbiguous(ex, ct))
        {
            return new LookupResult(LookupStatus.Unknown);
        }
    }

    public async Task<RefundResult> RefundAsync(Guid refundId, string pspReference, long amount, CancellationToken ct)
    {
        try
        {
            var refund = await _refunds.CreateAsync(
                new RefundCreateOptions { PaymentIntent = pspReference, Amount = amount },
                new RequestOptions { IdempotencyKey = refundId.ToString() },
                ct);
            return refund.Status is "succeeded" or "pending"
                ? new RefundResult(true, refund.Id)
                : new RefundResult(false, refund.Id, refund.FailureReason ?? refund.Status);
        }
        catch (StripeException ex) when (ex.HttpStatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            return new RefundResult(false, null, ex.StripeError?.Code ?? ex.Message);
        }
    }

    /// <summary>Anything where the request may have reached Stripe but we never saw the answer.</summary>
    private static bool IsAmbiguous(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested && ex switch
    {
        TaskCanceledException or TimeoutException or HttpRequestException => true,
        StripeException s => s.HttpStatusCode is 0 or >= HttpStatusCode.InternalServerError || s.InnerException is not null,
        _ => false,
    };
}
