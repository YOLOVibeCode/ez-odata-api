using EzOdata.Core.Services;
using Xunit;

namespace EzOdata.UnitTests.Core;

public class ServiceNameTests
{
    [Theory]
    [InlineData("sales")]
    [InlineData("sales_db")]
    [InlineData("sales-2")]
    [InlineData("a1")]
    public void Valid_slugs_are_accepted(string name) => Assert.True(ServiceName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]                  // too short (min 2)
    [InlineData("Sales")]              // uppercase
    [InlineData("1sales")]             // must start with a letter
    [InlineData("sales db")]           // whitespace
    [InlineData("sales/db")]           // path char
    [InlineData("_sales")]             // must start with a letter
    public void Invalid_names_are_rejected(string? name) => Assert.False(ServiceName.IsValid(name));

    [Fact]
    public void Names_longer_than_63_chars_are_rejected()
    {
        var name = "a" + new string('b', 63);
        Assert.False(ServiceName.IsValid(name));
    }
}
