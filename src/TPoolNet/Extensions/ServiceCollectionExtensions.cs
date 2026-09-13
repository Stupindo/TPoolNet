namespace TPoolNet.Extensions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPoolNet.Abstractions;
using TPoolNet.Data;
using TPoolNet.Options;
using TPoolNet.Services;

/// <summary>
/// Extension methods for registering TPoolNet services with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds TPoolNet services including <see cref="TPoolDbContext"/> and <see cref="ITablePoolService"/>
    /// to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <param name="configure">Optional configuration callback for <see cref="TPoolOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTPoolNet(
        this IServiceCollection services,
        Action<TPoolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TPoolOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddDbContext<TPoolDbContext>((sp, dbOptions) =>
        {
            var options = sp.GetRequiredService<IOptions<TPoolOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                dbOptions.UseSqlServer(options.ConnectionString);
            }
        });

        services.AddScoped<ITablePoolService, TablePoolService>();

        return services;
    }
}
