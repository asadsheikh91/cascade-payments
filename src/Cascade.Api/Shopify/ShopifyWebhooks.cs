using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cascade.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Shopify;

public sealed class ShopifyOptions
{
    /// <summary>App client secret(s). More than one while rotating: the new secret first, the old one until Shopify stops using it.</summary>
    public List<string> WebhookSecrets { get; set; } = [];
    public string? ShopDomain { get; set; }
    public string? AdminAccessToken { get; set; }
    public string AdminApiVersion { get; set; } = "2026-07";
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>Re-read this much before the cursor, to cover clock skew and orders committed out of order.</summary>
    public TimeSpan ReconcileOverlap { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxBodyBytes { get; set; } = 1024 * 1024;
}

public static class ShopifyHmac
{
    /// <summary>
    /// X-Shopify-Hmac-SHA256 is base64(HMAC-SHA256(client_secret, raw_body)). It must be computed over the exact bytes
    /// received: parsing and re-serialising the JSON changes whitespace/escaping and breaks the signature.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<byte> rawBody, string? headerValue, IEnumerable<string> secrets)
    {
        if (string.IsNullOrEmpty(headerValue)) return false;

        Span<byte> provided = stackalloc byte[32];
        if (!Convert.TryFromBase64String(headerValue, provided, out var written) || written != 32) return false;

        Span<byte> expected = stackalloc byte[32];
        foreach (var secret in secrets)
        {
            if (string.IsNullOrEmpty(secret)) continue;
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, expected);
            if (CryptographicOperations.FixedTimeEquals(expected, provided)) return true;
        }
        return false;
    }

    public static string Sign(byte[] rawBody, string secret) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody));
}

public static class ShopifyWebhookEndpoint
{
    public static void MapShopifyWebhooks(this IEndpointRouteBuilder app) =>
        app.MapPost("/webhooks/shopify", Receive).DisableAntiforgery();

    /// <summary>
    /// Shopify gives us 5 seconds (1 to connect). The only work done here: read the raw body, verify, one INSERT, 200.
    /// Duplicates also get a 200, otherwise Shopify keeps retrying something we already have.
    /// </summary>
    private static async Task<IResult> Receive(
        HttpContext http, CascadeDbContext db, IDatabaseDialect dialect, ShopifyOptions options, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var log = logs.CreateLogger("Cascade.Shopify.Webhook");

        if (http.Request.ContentLength > options.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var body = await ReadBodyAsync(http.Request.Body, options.MaxBodyBytes, ct);
        if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var headers = http.Request.Headers;
        if (!ShopifyHmac.IsValid(body, headers["X-Shopify-Hmac-SHA256"], options.WebhookSecrets))
        {
            log.LogWarning("Rejected Shopify webhook with invalid HMAC from {Shop}", (string?)headers["X-Shopify-Shop-Domain"]);
            return Results.Unauthorized();
        }

        string? webhookId = headers["X-Shopify-Webhook-Id"];
        string? topic = headers["X-Shopify-Topic"];
        string? shop = headers["X-Shopify-Shop-Domain"];
        if (string.IsNullOrEmpty(webhookId) || string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(shop))
            return Results.BadRequest("Missing X-Shopify-Webhook-Id, X-Shopify-Topic or X-Shopify-Shop-Domain.");

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(body);
            using var _ = System.Text.Json.JsonDocument.Parse(payload); // the payload column would reject it anyway (jsonb / ISJSON check); fail with 400 instead of 500
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or DecoderFallbackException)
        {
            return Results.BadRequest("Body is not valid JSON.");
        }

        var now = clock.GetUtcNow();
        var triggeredAt = DateTimeOffset.TryParse(headers["X-Shopify-Triggered-At"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : (DateTimeOffset?)null;
        string? eventId = headers["X-Shopify-Event-Id"];
        string? apiVersion = headers["X-Shopify-API-Version"];
        var inserted = await dialect.InsertDeliveryIfNewAsync(db, new WebhookDelivery
        {
            WebhookId = webhookId,
            EventId = eventId,
            Topic = topic,
            ShopDomain = shop,
            ApiVersion = apiVersion,
            TriggeredAt = triggeredAt,
            ReceivedAt = now,
            Payload = payload,
            Status = DeliveryStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
        }, ct);

        if (inserted == 0) log.LogInformation("Duplicate Shopify delivery {WebhookId} ignored", webhookId);
        return Results.Ok();
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
