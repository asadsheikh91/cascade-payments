using Cascade.Api.Data;
using Cascade.Api.Shopify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Cascade.Tests;

/// <summary>
/// One real database per test run (Testcontainers), shared by every integration test class.
/// Postgres by default; CASCADE_TEST_DB=sqlserver runs the same tests against SQL Server.
/// </summary>
public sealed class CascadeFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookSecret = "test-webhook-secret";
    public const string Shop = "cascade-dev.myshopify.com";

    public static readonly DatabaseProvider Provider =
        string.Equals(Environment.GetEnvironmentVariable("CASCADE_TEST_DB"), "sqlserver", StringComparison.OrdinalIgnoreCase)
            ? DatabaseProvider.SqlServer
            : DatabaseProvider.Postgres;

    private readonly IContainer _db;
    private readonly Func<string> _connectionString;

    public CascadeFactory()
    {
        if (Provider == DatabaseProvider.SqlServer)
        {
            var mssql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            _db = mssql;
            // The container's string points at master; migrations create the cascade database.
            _connectionString = () => new SqlConnectionStringBuilder(mssql.GetConnectionString()) { InitialCatalog = "cascade" }.ConnectionString;
        }
        else
        {
            var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            _db = postgres;
            _connectionString = () => postgres.GetConnectionString();
        }
    }

    public FakeShopifyAdminClient ShopifyAdmin { get; } = new();

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", Provider.ToString());
        builder.UseSetting("ConnectionStrings:Cascade", _connectionString());
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
