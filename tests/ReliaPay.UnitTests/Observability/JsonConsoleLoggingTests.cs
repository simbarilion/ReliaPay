using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Observability;

public sealed class JsonConsoleLoggingTests
{
    [Fact]
    public void Configure_json_console_accepts_logger_factory_and_safe_scope()
    {
        using ILoggerFactory factory = LoggerFactory.Create(ReliaPayLogging.ConfigureJsonConsole);
        ILogger logger = factory.CreateLogger("ReliaPay.Test");
        RequestTrace trace = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "req-hash",
            "/api/payment/request",
            "payment",
            "request");
        using (logger.BeginScope(ReliaPayLogging.ToScope(trace)))
        {
            logger.IsEnabled(LogLevel.Information).Should().BeTrue();
            logger.LogInformation("HTTP {httpMethod} {httpRoute} started", "POST", trace.Route);
        }

        IReadOnlyDictionary<string, object> scope = ReliaPayLogging.ToScope(trace);
        string json = JsonSerializer.Serialize(scope);
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        json.Should().Contain("correlationId");
        json.Should().Contain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        json.Should().NotContain("Bearer");
        json.Should().NotContain("password");
    }
}
