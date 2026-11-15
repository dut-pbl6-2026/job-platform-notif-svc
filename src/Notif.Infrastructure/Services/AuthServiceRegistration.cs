using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notif.Core.Interfaces;

namespace Notif.Infrastructure.Services;

public static class AuthServiceRegistration
{
    /// <summary>
    /// Validates the auth-svc settings (fails outside Development when they are missing) and registers
    /// <see cref="IUserEmailLookup"/> backed by <see cref="AuthUserClient"/>.
    /// </summary>
    public static IServiceCollection AddAuthServiceClient(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var options = AuthServiceOptions.Load(configuration, isDevelopment);
        services.AddSingleton(options);
        services.AddSingleton<IUserEmailLookup>(sp => new AuthUserClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
            sp.GetRequiredService<AuthServiceOptions>(),
            sp.GetRequiredService<ILogger<AuthUserClient>>()));
        return services;
    }
}
