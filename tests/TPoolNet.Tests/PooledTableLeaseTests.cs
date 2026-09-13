namespace TPoolNet.Tests;

using FluentAssertions;
using TPoolNet.Services;

public class PooledTableLeaseTests
{
    [Fact]
    public void Constructor_WithNullCallback_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PooledTableLease(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Properties_ForTransientLease_ReturnExpectedValues()
    {
        // Arrange
        var bookedAt = DateTime.UtcNow;
        await using var lease = new PooledTableLease((_, _) => Task.CompletedTask)
        {
            TablePoolId = 42,
            SchemaName = "tpool",
            TableName = "tbl_Order_001",
            ConsumerId = "test-consumer",
            BookedAtUtc = bookedAt,
            DeadlineUtc = null
        };

        // Assert
        lease.TablePoolId.Should().Be(42);
        lease.SchemaName.Should().Be("tpool");
        lease.TableName.Should().Be("tbl_Order_001");
        lease.FullQualifiedName.Should().Be("[tpool].[tbl_Order_001]");
        lease.ConsumerId.Should().Be("test-consumer");
        lease.BookedAtUtc.Should().Be(bookedAt);
        lease.DeadlineUtc.Should().BeNull();
        lease.IsPersistent.Should().BeFalse();
    }

    [Fact]
    public async Task Properties_ForPersistentLease_ReturnExpectedValues()
    {
        // Arrange
        var bookedAt = DateTime.UtcNow;
        var deadline = bookedAt.AddHours(1);
        await using var lease = new PooledTableLease((_, _) => Task.CompletedTask)
        {
            TablePoolId = 100,
            SchemaName = "custom",
            TableName = "tbl_Batch_005",
            ConsumerId = "batch-worker",
            BookedAtUtc = bookedAt,
            DeadlineUtc = deadline
        };

        // Assert
        lease.TablePoolId.Should().Be(100);
        lease.SchemaName.Should().Be("custom");
        lease.TableName.Should().Be("tbl_Batch_005");
        lease.FullQualifiedName.Should().Be("[custom].[tbl_Batch_005]");
        lease.ConsumerId.Should().Be("batch-worker");
        lease.BookedAtUtc.Should().Be(bookedAt);
        lease.DeadlineUtc.Should().Be(deadline);
        lease.IsPersistent.Should().BeTrue();
    }

    [Fact]
    public async Task TransientLease_OnDisposeAsync_InvokesReleaseCallbackWithDisposedReason()
    {
        // Arrange
        PooledTableLease? releasedLease = null;
        string? releaseReason = null;
        var callCount = 0;

        var lease = new PooledTableLease((l, reason) =>
        {
            callCount++;
            releasedLease = l;
            releaseReason = reason;
            return Task.CompletedTask;
        })
        {
            TablePoolId = 1,
            SchemaName = "tpool",
            TableName = "tbl_Test_001",
            ConsumerId = "consumer-1",
            BookedAtUtc = DateTime.UtcNow,
            DeadlineUtc = null
        };

        // Act
        await lease.DisposeAsync();

        // Assert
        callCount.Should().Be(1);
        releasedLease.Should().BeSameAs(lease);
        releaseReason.Should().Be("Disposed");
    }

    [Fact]
    public async Task TransientLease_MultipleDisposeAsync_InvokesCallbackOnlyOnce()
    {
        // Arrange
        var callCount = 0;
        var lease = new PooledTableLease((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.CompletedTask;
        })
        {
            TablePoolId = 1,
            SchemaName = "tpool",
            TableName = "tbl_Test_001",
            ConsumerId = "consumer-1",
            BookedAtUtc = DateTime.UtcNow,
            DeadlineUtc = null
        };

        // Act
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // Assert
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task PersistentLease_OnDisposeAsync_DoesNotInvokeReleaseCallback()
    {
        // Arrange
        var callCount = 0;
        var lease = new PooledTableLease((_, _) =>
        {
            callCount++;
            return Task.CompletedTask;
        })
        {
            TablePoolId = 1,
            SchemaName = "tpool",
            TableName = "tbl_Test_001",
            ConsumerId = "consumer-1",
            BookedAtUtc = DateTime.UtcNow,
            DeadlineUtc = DateTime.UtcNow.AddMinutes(30)
        };

        // Act
        await lease.DisposeAsync();

        // Assert
        callCount.Should().Be(0);
    }

    [Fact]
    public async Task PersistentLease_MultipleDisposeAsync_NeverInvokesReleaseCallback()
    {
        // Arrange
        var callCount = 0;
        var lease = new PooledTableLease((_, _) =>
        {
            callCount++;
            return Task.CompletedTask;
        })
        {
            TablePoolId = 1,
            SchemaName = "tpool",
            TableName = "tbl_Test_001",
            ConsumerId = "consumer-1",
            BookedAtUtc = DateTime.UtcNow,
            DeadlineUtc = DateTime.UtcNow.AddMinutes(30)
        };

        // Act
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // Assert
        callCount.Should().Be(0);
    }
}
