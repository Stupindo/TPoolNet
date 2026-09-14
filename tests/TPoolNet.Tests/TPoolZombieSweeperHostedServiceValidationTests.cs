namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TPoolNet.Options;
using TPoolNet.Services;

public class TPoolZombieSweeperHostedServiceValidationTests
{
    [Fact]
    public void Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TPoolOptions());
        var logger = NullLogger<TPoolZombieSweeperHostedService>.Instance;

        var act = () => new TPoolZombieSweeperHostedService(null!, options, logger);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<TPoolZombieSweeperHostedService>.Instance;

        var act = () => new TPoolZombieSweeperHostedService(scopeFactory, null!, logger);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new TPoolOptions());

        var act = () => new TPoolZombieSweeperHostedService(scopeFactory, options, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SweepAsync_NullContext_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new TPoolOptions());
        var logger = NullLogger<TPoolZombieSweeperHostedService>.Instance;

        var sweeper = new TPoolZombieSweeperHostedService(scopeFactory, options, logger);
        var act = () => sweeper.SweepAsync((Data.TPoolDbContext)null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
