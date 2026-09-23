namespace Cascade.Api.Shopify;

public enum DeliveryStatus
{
    Pending,
    Processed,
    /// <summary>Another delivery of the same event (e.g. from a second subscription) was already processed.</summary>
    DuplicateEvent,
    /// <summary>Topic we do not handle; kept for audit.</summary>
    Ignored,
    Dead,
}

/// <summary>
/// Inbox row: one per delivery, keyed by X-Shopify-Webhook-Id so Shopify's retries collapse onto one row.
/// Written inside the 5-second window; everything else happens later in <see cref="ShopifyInboxProcessor"/>.
/// </summary>
public sealed class WebhookDelivery
{
    public string WebhookId { get; set; } = "";
    public string? EventId { get; set; }
    public string Topic { get; set; } = "";
    public string ShopDomain { get; set; } = "";
    public string? ApiVersion { get; set; }
    public DateTimeOffset? TriggeredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Payload { get; set; } = "{}";
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>What "the same business event" means, per ADR 0001. Falls back to the delivery id if Shopify omits the event id.</summary>
    public string EventKey => $"{ShopDomain}:{EventId ?? "delivery:" + WebhookId}";
}

public sealed class ProcessedShopifyEvent
{
    public string IdempotencyKey { get; set; } = "";
    public string WebhookId { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Our projection of a Shopify order. Only moves forward in Shopify's updated_at time.</summary>
public sealed class ShopifyOrder
{
    public string ShopDomain { get; set; } = "";
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FinancialStatus { get; set; } = "";
    public string? FulfillmentStatus { get; set; }
    public bool Cancelled { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset ShopifyUpdatedAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
    public string LastSource { get; set; } = "";
}

public sealed class SyncCursor
{
    public string Name { get; set; } = "";
    public DateTimeOffset Value { get; set; }
}

public sealed class ShopifyReconciliationRun
{
    public Guid Id { get; set; }
    public string ShopDomain { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset Since { get; set; }
    public int Fetched { get; set; }
    /// <summary>Orders where the fetch found newer state than webhooks had delivered: missed or late events.</summary>
    public int Repaired { get; set; }
    public string? Error { get; set; }
}
