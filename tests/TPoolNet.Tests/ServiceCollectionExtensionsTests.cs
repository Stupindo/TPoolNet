namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TPoolNet.Abstractions;
using TPoolNet.Data;
using TPoolNet.Extensions;
using TPoolNet.Options;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTPoolNet_WithNullServices_ThrowsArgumentNullException()
    {
        // Act
        var act = () => ServiceCollectionExtensions.AddTPoolNet(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddTPoolNet_RegistersOptionsAndServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddTPoolNet(options =>
        {
            options.ConnectionString = "Server=localhost;Database=Test;Integrated Security=true;";
            options.HeartbeatTimeoutSeconds = 180;
            options.SweeperIntervalSeconds = 45;
        });

        // Assert
        var serviceDescriptors = services.ToList();

        serviceDescriptors.Should().Contain(d => d.ServiceType == typeof(ITablePoolService));
        serviceDescriptors.Should().Contain(d => d.ServiceType == typeof(ITableProvisionerService));
        serviceDescriptors.Should().Contain(d => d.ServiceType == typeof(TPoolDbContext));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TPoolOptions>>().Value;

        options.ConnectionString.Should().Be("Server=localhost;Database=Test;Integrated Security=true;");
        options.HeartbeatTimeoutSeconds.Should().Be(180);
        options.SweeperIntervalSeconds.Should().Be(45);
    }

    [Fact]
    public void AddTPoolNet_UsesDefaultOptionsWhenNotConfigured()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddTPoolNet();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TPoolOptions>>().Value;

        // Assert
        options.HeartbeatTimeoutSeconds.Should().Be(120);
        options.SweeperIntervalSeconds.Should().Be(60);
        options.ConnectionString.Should().BeEmpty();
    }
}
