using Microsoft.EntityFrameworkCore;

namespace Hookline.Api.Data;

public sealed class HooklineDbContext(DbContextOptions<HooklineDbContext> options) : DbContext(options)
{
    public const string IdempotencyIndex = "ix_events_idempotency_key";

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Delivery> Deliveries => Set<Delivery>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Subscription>(subscription =>
        {
            subscription.Property(s => s.Id).HasMaxLength(40);
            subscription.Property(s => s.Url).HasMaxLength(2000);
            subscription.Property(s => s.Secret).HasMaxLength(100);
            subscription.Property(s => s.DisabledReason).HasMaxLength(500);
        });

        model.Entity<Event>(@event =>
        {
            @event.Property(e => e.Id).HasMaxLength(40);
            @event.Property(e => e.Type).HasMaxLength(100);
            @event.Property(e => e.IdempotencyKey).HasMaxLength(255);

            // PostgreSQL treats every null as distinct, so only events that came with a key are held unique.
            @event.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyIndex);
        });

        model.Entity<Delivery>(delivery =>
        {
            delivery.Property(d => d.Id).HasMaxLength(40);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.LastError).HasMaxLength(2000);
            delivery.HasOne(d => d.Event).WithMany().HasForeignKey(d => d.EventId);
            delivery.HasOne(d => d.Subscription).WithMany().HasForeignKey(d => d.SubscriptionId);
            delivery.HasMany(d => d.AttemptLog).WithOne().HasForeignKey(a => a.DeliveryId);

            // What the worker asks for on every pass: pending deliveries that are due.
            delivery.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'");
        });

        model.Entity<DeliveryAttempt>(attempt =>
        {
            attempt.Property(a => a.Error).HasMaxLength(2000);
            attempt.Property(a => a.ResponseExcerpt).HasMaxLength(1000);
            attempt.HasIndex(a => new { a.DeliveryId, a.Number }).IsUnique();
        });
    }
}
