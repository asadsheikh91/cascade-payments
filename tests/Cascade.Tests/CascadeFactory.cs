using Cascade.Api.Data;
using Cascade.Api.Shopify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Cascade.Tests;

/// <summary>One real Postgres per test run (Testcontainers), shared by every integration test class.</summary>
public sealed class CascadeFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookSecret = "test-webhook-secret";
    public const string Shop = "cascade-dev.myshopify.com";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public FakeShopifyAdminClient ShopifyAdmin { get; } = new();

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Cascade", _db.GetConnectionString());
        builder.UseSetting("Workers:Enabled", "false"); // tests drive the processor explicitly
        builder.UseSetting("Shopify:WebhookSecrets:0", WebhookSecret);
        builder.UseSetting("Shopify:ShopDomain", Shop);
        builder.UseSetting("Payments:NotFoundSettleWindow", "00:00:00");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IShopifyAdminClient>();
            s.AddSingleton<IShopifyAdminClient>(ShopifyAdmin);
        });
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> WithDbAsync<T>(Func<CascadeDbContext, Task<T>> action) =>
        WithScopeAsync(sp => action(sp.GetRequiredService<CascadeDbContext>()));
}

public sealed class FakeShopifyAdminClient : IShopifyAdminClient
{
    public List<OrderSnapshot> Orders { get; } = [];

    public async IAsyncEnumerable<OrderSnapshot> OrdersUpdatedSinceAsync(DateTimeOffset since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var o in Orders.Where(o => o.UpdatedAt >= since).ToList())
        {
            await Task.Yield();
            yield return o;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<CascadeFactory>
{
    public const string Name = "db";
}
