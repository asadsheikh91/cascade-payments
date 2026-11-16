using System.Text.Json.Serialization;
using Cascade.Api;
using Cascade.Api.Data;
using Cascade.Api.Payments;
using Cascade.Api.Psp;
using Cascade.Api.Shopify;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
// Postgres by default; Database:Provider=SqlServer switches the context, migrations and SQL dialect.
builder.Services.AddCascadeDatabase(config);

builder.Services.AddSingleton(config.GetSection("Payments").Get<PaymentOptions>() ?? new PaymentOptions());
builder.Services.AddSingleton(config.GetSection("Shopify").Get<ShopifyOptions>() ?? new ShopifyOptions());
var stripe = config.GetSection("Stripe").Get<StripeOptions>() ?? new StripeOptions();
builder.Services.AddSingleton(stripe);

// PSPs. The simulator is always available; Stripe only when a test key is configured.
builder.Services.AddSingleton<IPaymentProvider>(new SimulatedProvider());
if (!string.IsNullOrWhiteSpace(stripe.SecretKey))
{
    builder.Services.AddHttpClient("stripe");
    builder.Services.AddSingleton<IPaymentProvider>(sp => new StripeProvider(
        stripe, sp.GetRequiredService<IHttpClientFactory>().CreateClient("stripe"), sp.GetRequiredService<ILogger<StripeProvider>>()));
}
builder.Services.AddSingleton<PaymentProviderRegistry>();
builder.Services.AddScoped<PaymentService>();

builder.Services.AddScoped<ShopifyOrderProjector>();
builder.Services.AddScoped<ShopifyInboxProcessor>();
builder.Services.AddScoped<ShopifyReconciler>();
builder.Services.AddHttpClient<IShopifyAdminClient, ShopifyAdminClient>(c => c.Timeout = TimeSpan.FromSeconds(30));

if (config.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<ShopifyInboxWorker>();
    builder.Services.AddHostedService<ShopifyReconcileWorker>();
}

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CascadeDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/health", async (CascadeDbContext db, PaymentProviderRegistry psps, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok", psps = psps.Names })
        : Results.Problem("database unreachable", statusCode: 503));
app.MapPayments();
app.MapShopifyWebhooks();

app.Run();

public partial class Program;
