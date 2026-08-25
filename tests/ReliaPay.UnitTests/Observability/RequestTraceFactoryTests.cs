using FluentAssertions;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Observability;

public sealed class RequestTraceFactoryTests
{
    [Fact]
    public void Gateway_mints_correlation_id_and_ignores_incoming_header()
    {
        Guid incoming = Guid.Parse("11111111-1111-1111-1111-111111111111");
        RequestTrace trace = RequestTraceFactory.Create(
            "/api/payment/request",
            incoming.ToString(),
            "key-1",
            mintCorrelationId: true);
        trace.CorrelationId.Should().NotBe(incoming);
        trace.CorrelationId.Should().NotBe(Guid.Empty);
        trace.RequestId.Should().Be("key-1");
        trace.Module.Should().Be("payment");
        trace.Action.Should().Be("request");
        trace.Route.Should().Be("/api/payment/request");
    }

    [Fact]
    public void Api_reuses_valid_incoming_correlation_id()
    {
        Guid incoming = Guid.Parse("22222222-2222-2222-2222-222222222222");
        RequestTrace trace = RequestTraceFactory.Create(
            "/api/operation/get",
            incoming.ToString(),
            null,
            mintCorrelationId: false);
        trace.CorrelationId.Should().Be(incoming);
        trace.RequestId.Should().BeNull();
        trace.Module.Should().Be("operation");
        trace.Action.Should().Be("get");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Api_mints_when_incoming_correlation_id_is_invalid(string? incoming)
    {
        RequestTrace trace = RequestTraceFactory.Create(
            "/api/payment/request",
            incoming,
            "abc",
            mintCorrelationId: false);
        trace.CorrelationId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Parses_module_and_action_from_openapi_version_path()
    {
        RequestTrace trace = RequestTraceFactory.Create(
            "/openapi/actions/payment/request/1.json",
            null,
            null,
            mintCorrelationId: true);
        trace.Module.Should().Be("payment");
        trace.Action.Should().Be("request");
        trace.Route.Should().Be("/openapi/actions/payment/request/1.json");
    }
}
