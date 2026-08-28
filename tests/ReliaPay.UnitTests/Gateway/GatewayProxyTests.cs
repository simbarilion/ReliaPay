using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ReliaPay.Contracts;
using ReliaPay.Gateway.Configuration;
using ReliaPay.Gateway.Proxy;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Gateway;

public sealed class GatewayProxyTests
{
    [Fact]
    public async Task Forwards_contract_headers_method_path_and_body_without_host()
    {
        RecordingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ok"}""", Encoding.UTF8, "application/json"),
            },
        };
        DefaultHttpContext context = CreateContext(
            "POST",
            "/api/payment/request",
            """{"operationKind":"PAYMENT_EXECUTION"}""");
        context.Request.Headers.Authorization = "Bearer test-token";
        context.Request.Headers["Idempotency-Key"] = "key-1";
        context.Request.Headers["X-Action-Version"] = "1";
        context.Request.Headers.Cookie = "session=secret";
        context.Request.Headers[RequestTraceFactory.CorrelationHeaderName] = "11111111-1111-1111-1111-111111111111";
        context.Request.ContentType = "application/json";
        Guid correlationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        ReliaPayTraceHttp.Set(
            context,
            new RequestTrace(correlationId, "key-1", "/api/payment/request", "payment", "request"));

        await CreateProxy(handler).ProxyAsync(context, CancellationToken.None);

        handler.Method.Should().Be("POST");
        handler.RequestUri!.AbsolutePath.Should().Be("/api/payment/request");
        handler.RequestUri.Host.Should().Be("api");
        handler.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Equal("Bearer test-token");
        handler.Headers.Should().ContainKey("Idempotency-Key").WhoseValue.Should().Equal("key-1");
        handler.Headers.Should().ContainKey("X-Action-Version").WhoseValue.Should().Equal("1");
        handler.Headers.Should().ContainKey(RequestTraceFactory.CorrelationHeaderName)
            .WhoseValue.Should().Equal(correlationId.ToString());
        handler.Headers.Should().ContainKey("Content-Type").WhoseValue.Should().Contain(value => value.Contains("application/json", StringComparison.Ordinal));
        handler.Headers.Should().NotContainKey("Cookie");
        if (handler.Headers.TryGetValue("Host", out string[]? host))
        {
            string joined = string.Join(',', host);
            joined.Should().NotContain("localhost");
            joined.Should().Contain("api");
        }

        handler.Body.Should().Be("""{"operationKind":"PAYMENT_EXECUTION"}""");
        context.Response.StatusCode.Should().Be(200);
        ReadBody(context).Should().Be("""{"status":"ok"}""");
    }

    [Fact]
    public async Task Returns_dependency_unavailable_without_internal_address_when_api_is_down()
    {
        RecordingHandler handler = new()
        {
            Throw = new HttpRequestException("connection refused"),
        };
        DefaultHttpContext context = CreateContext("GET", "/openapi/default.json");

        await CreateProxy(handler).ProxyAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        using JsonDocument document = JsonDocument.Parse(ReadBody(context));
        document.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.DependencyUnavailable);
        document.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("message").GetString().Should().NotContain("api:8080");
        document.RootElement.GetProperty("message").GetString().Should().NotContain("localhost");
    }

    [Fact]
    public async Task Maps_proxy_timeout_to_action_timeout_not_dependency_unavailable()
    {
        RecordingHandler handler = new()
        {
            Throw = new TaskCanceledException("request timed out"),
        };
        DefaultHttpContext context = CreateContext("POST", "/api/payment/request", "{}");

        await CreateProxy(handler).ProxyAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
        using JsonDocument document = JsonDocument.Parse(ReadBody(context));
        document.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ActionTimeout);
        document.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Forwards_openapi_get_without_request_body()
    {
        RecordingHandler handler = new()
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"openapi":"3.0.3"}""", Encoding.UTF8, "application/json"),
            },
        };
        DefaultHttpContext context = CreateContext("GET", "/openapi/actions/payment/request/1.json");

        await CreateProxy(handler).ProxyAsync(context, CancellationToken.None);

        handler.Method.Should().Be("GET");
        handler.RequestUri!.AbsolutePath.Should().Be("/openapi/actions/payment/request/1.json");
        handler.Body.Should().BeEmpty();
        context.Response.StatusCode.Should().Be(200);
        ReadBody(context).Should().Be("""{"openapi":"3.0.3"}""");
    }

    private static GatewayProxy CreateProxy(RecordingHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://api:8080/") };
        GatewayOptions options = new() { ApiBaseUrl = new Uri("http://api:8080") };
        return new GatewayProxy(client, options);
    }

    private static DefaultHttpContext CreateContext(string method, string path, string? body = null)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 8080);
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }

        return context;
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
