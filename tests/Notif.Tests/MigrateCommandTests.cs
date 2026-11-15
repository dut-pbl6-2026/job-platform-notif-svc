using Notif.Api.Bootstrap;
using Xunit;

namespace Notif.Tests;

public class MigrateCommandTests
{
    [Fact]
    public void IsInvoked_OnlyForFirstArgumentMigrate()
    {
        Assert.True(MigrateCommand.IsInvoked(["migrate"]));
        Assert.False(MigrateCommand.IsInvoked([]));
        Assert.False(MigrateCommand.IsInvoked(["serve"]));
    }

    [Fact]
    public async Task MissingConnectionString_Fails()
    {
        var stderr = new StringWriter();

        var exit = await MigrateCommand.RunAsync(["migrate"], null, new StringWriter(), stderr);

        Assert.Equal(1, exit);
        Assert.Contains("connection string missing", stderr.ToString());
    }

    [Fact]
    public async Task ExtraArguments_AreRejected()
    {
        var stderr = new StringWriter();

        var exit = await MigrateCommand.RunAsync(["migrate", "now"], "Host=unused;Database=unused", new StringWriter(), stderr);

        Assert.Equal(2, exit);
    }
}
