using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Observability;

public sealed class ReliaPayTraceMiddlewareTests
{
    [Fact]
    public async Task Logs_request_without_jwt_or_payload_and_stores_trace()
    {
        CollectingLogger logger = new();
        ReliaPayTraceMiddleware middleware = new(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("ok"));
            },
            logger,
            new ReliaPayTraceOptions { MintCorrelationId = true, ServiceName = "ReliaPay.Gateway" });
        DefaultHttpContext http = new();
        http.Request.Method = "POST";
        http.Request.Path = "/api/payment/request";
        http.Request.Headers.Authorization = "Bearer super-secret-jwt-token";
        http.Request.Headers["Idempotency-Key"] = "key-9";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"amount":"10.00"}"""));
        http.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(http);

        RequestTrace? trace = ReliaPayTraceHttp.Get(http);
        trace.Should().NotBeNull();
        trace!.Module.Should().Be("payment");
        trace.Action.Should().Be("request");
        trace.RequestId.Should().Be(RequestIdHash.Sha256Hex("key-9"));
        trace.RequestId.Should().NotBe("key-9");
        string logText = string.Join('\n', logger.Messages);
        logText.Should().Contain("/api/payment/request");
        logText.Should().Contain("200");
        logger.ScopeValues.Should().Contain(trace.CorrelationId.ToString("D"));
        logText.Should().NotContain("super-secret-jwt-token");
        logText.Should().NotContain("Bearer");
        logText.Should().NotContain("10.00");
        logText.Should().NotContain("key-9");
        string scopeText = string.Join(' ', logger.ScopeValues.Select(static value => value?.ToString() ?? string.Empty));
        scopeText.Should().NotContain("super-secret-jwt-token");
        scopeText.Should().NotContain("Bearer");
    }

    [Fact]
    public async Task Api_mode_reuses_correlation_header()
    {
        Guid incoming = Guid.Parse("33333333-3333-3333-3333-333333333333");
        ReliaPayTraceMiddleware middleware = new(
            _ => Task.CompletedTask,
            new CollectingLogger(),
            new ReliaPayTraceOptions { MintCorrelationId = false, ServiceName = "ReliaPay.Api" });
        DefaultHttpContext http = new();
        http.Request.Method = "POST";
        http.Request.Path = "/api/payment/request";
        http.Request.Headers[RequestTraceFactory.CorrelationHeaderName] = incoming.ToString();

        await middleware.InvokeAsync(http);

        ReliaPayTraceHttp.Get(http)!.CorrelationId.Should().Be(incoming);
    }

    private sealed class CollectingLogger : ILogger<ReliaPayTraceMiddleware>
    {
        public List<string> Messages { get; } = [];

        public List<object?> ScopeValues { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (KeyValuePair<string, object> pair in pairs)
                {
                    ScopeValues.Add(pair.Value);
                }
            }
            else if (state is IEnumerable<KeyValuePair<string, object?>> nullablePairs)
            {
                foreach (KeyValuePair<string, object?> pair in nullablePairs)
                {
                    ScopeValues.Add(pair.Value);
                }
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
