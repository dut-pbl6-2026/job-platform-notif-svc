using Microsoft.EntityFrameworkCore;
using Notif.Core.Entities;

namespace Notif.Infrastructure.Data;

public class NotifDbContext : DbContext
{
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public NotifDbContext(DbContextOptions<NotifDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationLog>(b =>
        {
            b.ToTable("notification_logs");
            b.HasKey(e => e.Id);
            b.HasIndex(e => new { e.ApplicationId, e.EventType, e.StatusSnapshot }).IsUnique();
            b.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            b.Property(e => e.StatusSnapshot).HasMaxLength(64);
            b.Property(e => e.RecipientEmail).HasMaxLength(256).IsRequired();
            b.Property(e => e.Subject).HasMaxLength(256).IsRequired();
            b.Property(e => e.TemplateName).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(16).IsRequired();
            b.Property(e => e.ErrorMessage).HasMaxLength(2000);
        });
    }
}
