using FluentAssertions;
using ReliaPay.Gateway.Routing;

namespace ReliaPay.UnitTests.Gateway;

public sealed class GatewayRouteWhitelistTests
{
    private readonly GatewayRouteWhitelist _whitelist = new();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/HEALTH/LIVE")]
    [InlineData("/openapi/default.json")]
    [InlineData("/api/payment/request")]
    [InlineData("/API/payment/request")]
    [InlineData("/api/operation/get")]
    [InlineData("/openapi/actions/payment/request/1.json")]
    [InlineData("/openapi/actions/operation/get/2.json")]
    [InlineData("/api/payment/request?x=1")]
    public void IsAllowed_accepts_contract_routes(string path)
    {
        _whitelist.IsAllowed(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/swagger")]
    [InlineData("/api")]
    [InlineData("/api/payment")]
    [InlineData("/api/payment/request/extra")]
    [InlineData("/api/payment/request/")]
    [InlineData("/openapi/default")]
    [InlineData("/openapi/actions/payment/request/v1.json")]
    [InlineData("/metrics")]
    public void IsAllowed_rejects_unknown_routes(string path)
    {
        _whitelist.IsAllowed(path).Should().BeFalse();
    }
}
