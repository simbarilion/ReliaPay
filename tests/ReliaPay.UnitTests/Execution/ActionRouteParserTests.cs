using FluentAssertions;
using ReliaPay.Execution;

namespace ReliaPay.UnitTests.Execution;

public sealed class ActionRouteParserTests
{
    [Fact]
    public void Parses_two_segments_and_optional_version()
    {
        ActionRouteParser.TryParse("/api/payment/request", null, out ActionRoute? without).Should().BeTrue();
        without!.Module.Should().Be("payment");
        without.Action.Should().Be("request");
        without.ExplicitVersion.Should().BeNull();

        ActionRouteParser.TryParse("/api/operation/get", "1", out ActionRoute? with).Should().BeTrue();
        with!.ExplicitVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("/api/payment")]
    [InlineData("/api/payment/request/extra")]
    [InlineData("/api/payment/request/")]
    [InlineData("/payment/request")]
    [InlineData("/api/payment/request", "v1")]
    [InlineData("/api/payment/request", "0")]
    public void Rejects_invalid_route_or_version(string path, string? version = null)
    {
        ActionRouteParser.TryParse(path, version, out _).Should().BeFalse();
    }
}
