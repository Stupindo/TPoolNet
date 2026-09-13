namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TPoolNet.Data;
using TPoolNet.Exceptions;
using TPoolNet.Services;

public class TableProvisionerServiceValidationTests
{
    private static TableProvisionerService CreateService()
    {
        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer("Server=dummy;Database=dummy;")
            .Options;
        var context = new TPoolDbContext(options);
        return new TableProvisionerService(context);
    }

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        var act = () => new TableProvisionerService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProvisionTablesAsync_NullOrWhitespaceTableType_ThrowsArgumentException(string? tableTypeName)
    {
        var service = CreateService();
        var act = () => service.ProvisionTablesAsync(tableTypeName!, 1);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task ProvisionTablesAsync_NonPositiveCount_ThrowsArgumentOutOfRangeException(int count)
    {
        var service = CreateService();
        var act = () => service.ProvisionTablesAsync("OrderPool", count);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(count));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DropTablesAsync_NullOrWhitespaceTableType_ThrowsArgumentException(string? tableTypeName)
    {
        var service = CreateService();
        var act = () => service.DropTablesAsync(tableTypeName!, 1);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task DropTablesAsync_NonPositiveCount_ThrowsArgumentOutOfRangeException(int count)
    {
        var service = CreateService();
        var act = () => service.DropTablesAsync("OrderPool", count);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(count));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnsurePoolCapacityAsync_NullOrWhitespaceTableType_ThrowsArgumentException(string? tableTypeName)
    {
        var service = CreateService();
        var act = () => service.EnsurePoolCapacityAsync(tableTypeName!, 5);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task EnsurePoolCapacityAsync_NegativeTargetCapacity_ThrowsArgumentOutOfRangeException(int capacity)
    {
        var service = CreateService();
        var act = () => service.EnsurePoolCapacityAsync("OrderPool", capacity);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("targetCapacity");
    }
}
