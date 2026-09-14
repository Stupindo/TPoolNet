namespace TPoolNet.Tests;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TPoolNet.Data;
using TPoolNet.Services;

public class TablePoolServiceValidationTests
{
    private static TablePoolService CreateService()
    {
        var options = new DbContextOptionsBuilder<TPoolDbContext>()
            .UseSqlServer("Server=dummy;Database=dummy;")
            .Options;
        var context = new TPoolDbContext(options);
        return new TablePoolService(context);
    }

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        var act = () => new TablePoolService(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BookPersistentAsync_NullOrWhitespaceTableType_ThrowsArgumentException(string? tableTypeName)
    {
        var service = CreateService();
        var act = () => service.BookPersistentAsync(tableTypeName!, "consumer-1", TimeSpan.FromMinutes(10));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BookPersistentAsync_NullOrWhitespaceConsumerId_ThrowsArgumentException(string? consumerId)
    {
        var service = CreateService();
        var act = () => service.BookPersistentAsync("OrderPool", consumerId!, TimeSpan.FromMinutes(10));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task BookPersistentAsync_NonPositiveRetentionPeriod_ThrowsArgumentOutOfRangeException(int seconds)
    {
        var service = CreateService();
        var act = () => service.BookPersistentAsync("OrderPool", "consumer-1", TimeSpan.FromSeconds(seconds));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("retentionPeriod");
    }

    [Fact]
    public async Task BookPersistentAsync_ExcessiveRetentionPeriod_ThrowsArgumentOutOfRangeException()
    {
        var service = CreateService();
        var act = () => service.BookPersistentAsync("OrderPool", "consumer-1", TimeSpan.FromDays(365 * 11));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("retentionPeriod");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BookAsync_NullOrWhitespaceTableType_ThrowsArgumentException(string? tableTypeName)
    {
        var service = CreateService();
        var act = () => service.BookAsync(tableTypeName!, "consumer-1");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BookAsync_NullOrWhitespaceConsumerId_ThrowsArgumentException(string? consumerId)
    {
        var service = CreateService();
        var act = () => service.BookAsync("OrderPool", consumerId!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReleaseAsync_NullOrWhitespaceTableName_ThrowsArgumentException(string? tableName)
    {
        var service = CreateService();
        var act = () => service.ReleaseAsync(tableName!, "consumer-1");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReleaseAsync_NullOrWhitespaceConsumerId_ThrowsArgumentException(string? consumerId)
    {
        var service = CreateService();
        var act = () => service.ReleaseAsync("tbl_Order_001", consumerId!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("too.many.parts.here")]
    [InlineData("tbl_Order; DROP TABLE Users--")]
    [InlineData("tpool.[invalid space table]")]
    public async Task ReleaseAsync_MalformedTableName_ThrowsArgumentException(string tableName)
    {
        var service = CreateService();
        var act = () => service.ReleaseAsync(tableName, "consumer-1");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public async Task SendHeartbeatAsync_NonPositiveTablePoolId_ThrowsArgumentOutOfRangeException(long tablePoolId)
    {
        var service = CreateService();
        var act = () => service.SendHeartbeatAsync(tablePoolId);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(tablePoolId));
    }
}
