using Microsoft.EntityFrameworkCore;
using Notif.Core.Entities;

namespace Notif.Infrastructure.Data;

public class NotifDbContext : DbContext
{
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();

    public NotifDbContext(DbContextOptions<NotifDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailLog>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasIndex(e => e.EventId).IsUnique();
            b.Property(e => e.RecipientEmail).HasMaxLength(256).IsRequired();
            b.Property(e => e.Subject).HasMaxLength(256).IsRequired();
            b.Property(e => e.TemplateName).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(16).IsRequired();
            b.Property(e => e.ErrorMessage).HasMaxLength(2000);
        });
    }
}
