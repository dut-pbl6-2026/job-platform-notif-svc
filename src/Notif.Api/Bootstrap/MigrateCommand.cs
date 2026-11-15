using Microsoft.EntityFrameworkCore;
using Notif.Infrastructure.Data;

namespace Notif.Api.Bootstrap;

/// <summary>
/// One-off <c>migrate</c> command. Applies pending EF Core migrations to the NotifDb database and exits.
/// It is the schema deploy step: the web host never migrates on startup.
/// </summary>
public static class MigrateCommand
{
    public const string Name = "migrate";

    /// <summary>True when the process was started with the command name as its first argument.</summary>
    public static bool IsInvoked(string[] args) => args.Length > 0 && args[0] == Name;

    /// <summary>
    /// Runs the command and returns the process exit code: 0 when the database is up to date,
    /// 1 on configuration or database failure, 2 on unexpected extra arguments.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        string? connectionString,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default)
    {
        if (args.Length != 1)
        {
            stderr.WriteLine($"Usage: {Name}   (takes no further arguments)");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            stderr.WriteLine($"{Name} failed: connection string missing (set DATABASE_URL_NOTIF or ConnectionStrings:NotifDb)");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<NotifDbContext>().UseNpgsql(connectionString).Options;
            await using var db = new NotifDbContext(options);
            await db.Database.MigrateAsync(ct);
            stdout.WriteLine($"{Name}: database is up to date.");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"{Name} failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
