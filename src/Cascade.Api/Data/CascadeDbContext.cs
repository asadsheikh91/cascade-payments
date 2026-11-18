using Cascade.Api.Payments;
using Cascade.Api.Shopify;
using Microsoft.EntityFrameworkCore;

namespace Cascade.Api.Data;

/// <summary>
/// The Postgres model, and the shared model for every provider. SQL Server differences live in
/// <see cref="SqlServerCascadeDbContext"/>, which has its own migrations.
/// </summary>
public class CascadeDbContext : DbContext
{
    public CascadeDbContext(DbContextOptions<CascadeDbContext> options) : base(options) { }

    /// <summary>For provider-specific subclasses, which need their own options type.</summary>
    protected CascadeDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ProcessedShopifyEvent> ProcessedShopifyEvents => Set<ProcessedShopifyEvent>();
    public DbSet<ShopifyOrder> ShopifyOrders => Set<ShopifyOrder>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();
    public DbSet<ShopifyReconciliationRun> ShopifyReconciliationRuns => Set<ShopifyReconciliationRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Ids are UUIDv7 generated in the domain. Telling EF they are never store-generated makes a new child reached
        // through a navigation count as an INSERT; otherwise EF sees a non-empty key and issues an UPDATE instead.
        b.Entity<Payment>().Property(p => p.Id).ValueGeneratedNever();
        b.Entity<PaymentAttempt>().Property(a => a.Id).ValueGeneratedNever();
        b.Entity<Refund>().Property(r => r.Id).ValueGeneratedNever();
        b.Entity<ShopifyReconciliationRun>().Property(r => r.Id).ValueGeneratedNever();

        b.Entity<Payment>(e =>
        {
            e.HasIndex(p => p.IdempotencyKey).IsUnique();
            e.HasIndex(p => p.CreatedAt);
            e.Property(p => p.IdempotencyKey).HasMaxLength(255);
            e.Property(p => p.MerchantReference).HasMaxLength(255);
            e.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(p => p.PaymentMethod).HasMaxLength(255);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
            e.HasMany(p => p.Attempts).WithOne().HasForeignKey(a => a.PaymentId);
            e.HasMany(p => p.Refunds).WithOne().HasForeignKey(r => r.PaymentId);
            e.Navigation(p => p.Attempts).AutoInclude();
            e.Navigation(p => p.Refunds).AutoInclude();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_payments_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_payments_refunded_within_amount", "refunded_amount >= 0 AND refunded_amount <= amount");
            });
        });

        b.Entity<PaymentAttempt>(e =>
        {
            e.HasIndex(a => new { a.PaymentId, a.Sequence }).IsUnique();
            e.Property(a => a.Psp).HasMaxLength(32);
            e.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(32);
        });

        b.Entity<Refund>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            e.ToTable(t => t.HasCheckConstraint("ck_refunds_amount_positive", "amount > 0"));
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.HasKey(d => d.WebhookId);
            e.Property(d => d.WebhookId).HasMaxLength(255);
            e.Property(d => d.EventId).HasMaxLength(255);
            e.Property(d => d.Topic).HasMaxLength(128);
            e.Property(d => d.ShopDomain).HasMaxLength(255);
            e.Property(d => d.Payload).HasColumnType("jsonb");
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(d => new { d.Status, d.NextAttemptAt, d.TriggeredAt });
        });

        b.Entity<ProcessedShopifyEvent>(e =>
        {
            e.HasKey(x => x.IdempotencyKey);
            e.Property(x => x.IdempotencyKey).HasMaxLength(255);
        });

        b.Entity<ShopifyOrder>(e =>
        {
            e.HasKey(o => new { o.ShopDomain, o.Id });
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.TotalPrice).HasPrecision(18, 4);
            e.Property(o => o.Currency).HasMaxLength(3);
        });

        b.Entity<SyncCursor>().HasKey(c => c.Name);
    }
}
