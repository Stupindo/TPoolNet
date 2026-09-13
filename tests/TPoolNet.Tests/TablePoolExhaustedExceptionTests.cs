namespace TPoolNet.Tests;

using FluentAssertions;
using TPoolNet.Exceptions;

public class TablePoolExhaustedExceptionTests
{
    [Fact]
    public void Constructor_WithTypeName_SetsPropertiesCorrectly()
    {
        // Act
        var ex = new TablePoolExhaustedException("TestType");

        // Assert
        ex.TableTypeName.Should().Be("TestType");
        ex.Message.Should().Contain("TestType");
        ex.Should().BeAssignableTo<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_SetsPropertiesCorrectly()
    {
        // Arrange
        var inner = new InvalidOperationException("Root cause");

        // Act
        var ex = new TablePoolExhaustedException("TestType", "Custom message", inner);

        // Assert
        ex.TableTypeName.Should().Be("TestType");
        ex.Message.Should().Be("Custom message");
        ex.InnerException.Should().BeSameAs(inner);
    }
}
