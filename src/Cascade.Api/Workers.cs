using Cascade.Api.Payments;
using Cascade.Api.Shopify;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api;

public sealed class ShopifyInboxWorker(IServiceScopeFactory scopes, ILogger<ShopifyInboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                handled = await scope.ServiceProvider.GetRequiredService<ShopifyInboxProcessor>().ProcessBatchAsync(100, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Inbox worker iteration failed");
            }
            // Drain quickly while there is work; poll gently when idle.
            if (handled == 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
}

public sealed class ShopifyReconcileWorker(IServiceScopeFactory scopes, ShopifyOptions options, ILogger<ShopifyReconcileWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.ShopDomain) || string.IsNullOrEmpty(options.AdminAccessToken))
        {
            log.LogInformation("Shopify reconciliation disabled: Shopify:ShopDomain / Shopify:AdminAccessToken not set");
            return;
        }

        using var timer = new PeriodicTimer(options.ReconcileInterval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ShopifyReconciler>().RunAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Reconciliation iteration failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Domain exceptions become 4xx problem responses instead of 500s.</summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        (int status, string title)? mapped = ex switch
        {
            InvalidPaymentTransitionException => (StatusCodes.Status409Conflict, "Invalid payment state transition"),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrent modification, retry the request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => null,
        };
        if (mapped is null) return false;

        http.Response.StatusCode = mapped.Value.status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = ex,
            ProblemDetails = { Status = mapped.Value.status, Title = mapped.Value.title, Detail = ex.Message },
        });
    }
}
