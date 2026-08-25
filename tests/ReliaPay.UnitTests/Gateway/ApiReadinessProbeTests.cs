using System.Net;
using FluentAssertions;
using ReliaPay.Gateway.Configuration;
using ReliaPay.Gateway.Health;

namespace ReliaPay.UnitTests.Gateway;

public sealed class ApiReadinessProbeTests
{
    [Fact]
    public async Task Probes_api_ready_not_live()
    {
        RecordingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ready"}"""),
            },
        };
        ApiReadinessProbe probe = CreateProbe(handler);

        bool ready = await probe.IsApiReadyAsync(CancellationToken.None);

        ready.Should().BeTrue();
        handler.RequestUri!.AbsolutePath.Should().Be("/health/ready");
    }

    [Fact]
    public async Task Treats_api_unavailable_as_not_ready()
    {
        RecordingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"status":"unavailable"}"""),
            },
        };

        bool ready = await CreateProbe(handler).IsApiReadyAsync(CancellationToken.None);

        ready.Should().BeFalse();
    }

    [Fact]
    public async Task Treats_network_failure_as_not_ready()
    {
        RecordingHandler handler = new()
        {
            Throw = new HttpRequestException("connection refused"),
        };

        bool ready = await CreateProbe(handler).IsApiReadyAsync(CancellationToken.None);

        ready.Should().BeFalse();
    }

    private static ApiReadinessProbe CreateProbe(RecordingHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://api:8080/") };
        GatewayOptions options = new() { ApiBaseUrl = new Uri("http://api:8080") };
        return new ApiReadinessProbe(client, options);
    }
}
