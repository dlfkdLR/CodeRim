using System.Text.Json;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Tests;

public sealed class ProviderObjectTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"token\":1,\"Token\":2}", true)]
    [InlineData("{\"token\":1,\"token\":2}", false)]
    [InlineData("{\"token\":1,\"\\u0074oken\":2}", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("1", false)]
    public void NamesAreUniqueByDecodedOrdinalValue(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, ProviderParsers.IsUniqueObject(document.RootElement));
    }

    [Fact]
    public void MissingObjectIsRejected() => Assert.False(ProviderParsers.IsUniqueObject(default));
}
