namespace TPoolNet.Tests;

using FluentAssertions;
using TPoolNet.Exceptions;

public class ExceptionTests
{
    [Fact]
    public void ConsumerMismatchException_DefaultMessage_SetsPropertiesAndMessage()
    {
        // Arrange & Act
        var ex = new ConsumerMismatchException("tpool", "tbl_Test_001", "expected-worker", "actual-worker");

        // Assert
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.SchemaName.Should().Be("tpool");
        ex.TableName.Should().Be("tbl_Test_001");
        ex.ExpectedConsumerId.Should().Be("expected-worker");
        ex.ActualConsumerId.Should().Be("actual-worker");
        ex.Message.Should().Contain("Consumer mismatch");
        ex.Message.Should().Contain("[tpool].[tbl_Test_001]");
        ex.Message.Should().Contain("expected-worker");
        ex.Message.Should().Contain("actual-worker");
    }

    [Fact]
    public void ConsumerMismatchException_CustomMessageAndInnerException_Preserved()
    {
        // Arrange
        var inner = new InvalidOperationException("Root cause");

        // Act
        var ex = new ConsumerMismatchException("custom", "tbl_Batch_002", "exp", "act", "Custom failure message", inner);

        // Assert
        ex.SchemaName.Should().Be("custom");
        ex.TableName.Should().Be("tbl_Batch_002");
        ex.ExpectedConsumerId.Should().Be("exp");
        ex.ActualConsumerId.Should().Be("act");
        ex.Message.Should().Be("Custom failure message");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void TableLeaseNotFoundException_DefaultMessage_SetsPropertiesAndMessage()
    {
        // Arrange & Act
        var ex = new TableLeaseNotFoundException("tpool", "tbl_Orders_999");

        // Assert
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.SchemaName.Should().Be("tpool");
        ex.TableName.Should().Be("tbl_Orders_999");
        ex.Message.Should().Contain("No active lease found for table '[tpool].[tbl_Orders_999]'");
    }

    [Fact]
    public void TableLeaseNotFoundException_CustomMessageAndInnerException_Preserved()
    {
        // Arrange
        var inner = new InvalidOperationException("Root cause");

        // Act
        var ex = new TableLeaseNotFoundException("tpool", "tbl_Orders_999", "Custom not found message", inner);

        // Assert
        ex.SchemaName.Should().Be("tpool");
        ex.TableName.Should().Be("tbl_Orders_999");
        ex.Message.Should().Be("Custom not found message");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void TableTypeNotFoundException_DefaultMessage_SetsPropertiesAndMessage()
    {
        // Arrange & Act
        var ex = new TableTypeNotFoundException("NonExistentType");

        // Assert
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.TableTypeName.Should().Be("NonExistentType");
        ex.Message.Should().Contain("Table type 'NonExistentType' was not found.");
    }

    [Fact]
    public void TableTypeNotFoundException_CustomMessageAndInnerException_Preserved()
    {
        // Arrange
        var inner = new InvalidOperationException("Root cause");

        // Act
        var ex = new TableTypeNotFoundException("NonExistentType", "Custom type error", inner);

        // Assert
        ex.TableTypeName.Should().Be("NonExistentType");
        ex.Message.Should().Be("Custom type error");
        ex.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public void PoolCapacityExceededException_DefaultMessage_SetsPropertiesAndMessage()
    {
        // Arrange & Act
        var ex = new PoolCapacityExceededException("OrderPool", requestedCount: 5, currentCount: 8, maxPoolSize: 10);

        // Assert
        ex.Should().BeAssignableTo<InvalidOperationException>();
        ex.TableTypeName.Should().Be("OrderPool");
        ex.RequestedCount.Should().Be(5);
        ex.CurrentCount.Should().Be(8);
        ex.MaxPoolSize.Should().Be(10);
        ex.Message.Should().Contain("Cannot provision 5 table(s) for table type 'OrderPool'");
        ex.Message.Should().Contain("Current count is 8 and MaxPoolSize is 10.");
    }

    [Fact]
    public void PoolCapacityExceededException_CustomMessageAndInnerException_Preserved()
    {
        // Arrange
        var inner = new InvalidOperationException("Root cause");

        // Act
        var ex = new PoolCapacityExceededException("OrderPool", 5, 8, 10, "Custom capacity message", inner);

        // Assert
        ex.TableTypeName.Should().Be("OrderPool");
        ex.RequestedCount.Should().Be(5);
        ex.CurrentCount.Should().Be(8);
        ex.MaxPoolSize.Should().Be(10);
        ex.Message.Should().Be("Custom capacity message");
        ex.InnerException.Should().BeSameAs(inner);
    }
}
