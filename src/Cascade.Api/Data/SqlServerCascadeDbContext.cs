using Cascade.Api.Payments;
using Cascade.Api.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cascade.Api.Data;

/// <summary>
/// The same model on SQL Server. A separate context type gives SQL Server its own migrations
/// (Data/Migrations/SqlServer), because migrations hold provider-specific column types.
/// </summary>
public sealed class SqlServerCascadeDbContext(DbContextOptions<SqlServerCascadeDbContext> options) : CascadeDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Payment>(e =>
        {
            // Postgres uses the system column xmin. SQL Server has no equivalent, so a rowversion column is added
            // (shadow property, the domain never sees it); EF checks it on every UPDATE the same way.
            e.Ignore(p => p.Version);
            e.Property<byte[]>("RowVersion").IsRowVersion();
        });

        b.Entity<WebhookDelivery>(e =>
        {
            // jsonb rejects invalid JSON on insert; ISJSON gives SQL Server the same guarantee.
            e.Property(d => d.Payload).HasColumnType("nvarchar(max)");
            e.ToTable(t => t.HasCheckConstraint("ck_webhook_deliveries_payload_is_json", "ISJSON(payload) = 1"));
        });

        // Unbounded strings map to nvarchar(450) as keys on SQL Server; shop domains are short, keep the key narrow.
        b.Entity<ShopifyOrder>().Property(o => o.ShopDomain).HasMaxLength(255);
    }
}

/// <summary>Lets `dotnet ef migrations add --context SqlServerCascadeDbContext` build the model without a database.</summary>
public sealed class SqlServerCascadeDbContextFactory : IDesignTimeDbContextFactory<SqlServerCascadeDbContext>
{
    public SqlServerCascadeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerCascadeDbContext>()
            .UseSqlServer("Server=localhost;Database=cascade;Integrated Security=false")
            .UseSnakeCaseNamingConvention()
            .Options);
}

public enum DatabaseProvider { Postgres, SqlServer }

public static class DatabaseSetup
{
    /// <summary>Registers CascadeDbContext and the matching SQL dialect for Database:Provider (Postgres by default).</summary>
    public static DatabaseProvider AddCascadeDatabase(this IServiceCollection services, IConfiguration config)
    {
        var provider = config.GetValue("Database:Provider", DatabaseProvider.Postgres);
        var connectionString = config.GetConnectionString("Cascade");

        if (provider == DatabaseProvider.SqlServer)
        {
            services.AddDbContext<CascadeDbContext, SqlServerCascadeDbContext>(o => o
                .UseSqlServer(connectionString)
                .UseSnakeCaseNamingConvention());
            services.AddSingleton<IDatabaseDialect, SqlServerDialect>();
        }
        else
        {
            services.AddDbContext<CascadeDbContext>(o => o
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention());
            services.AddSingleton<IDatabaseDialect, PostgresDialect>();
        }
        return provider;
    }
}
