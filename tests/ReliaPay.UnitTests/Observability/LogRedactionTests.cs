using FluentAssertions;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Observability;

public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("X-Api-Key")]
    public void Redacts_sensitive_headers(string name)
    {
        LogRedaction.IsSensitiveHeader(name).Should().BeTrue();
        LogRedaction.RedactHeader(name, "Bearer secret.token.value").Should().Be("[redacted]");
    }

    [Theory]
    [InlineData("Content-Type", "application/json")]
    [InlineData("X-Action-Version", "1")]
    public void Keeps_non_secret_headers(string name, string value)
    {
        LogRedaction.IsSensitiveHeader(name).Should().BeFalse();
        LogRedaction.RedactHeader(name, value).Should().Be(value);
    }

    [Fact]
    public void Redacts_idempotency_key_header()
    {
        LogRedaction.IsSensitiveHeader("Idempotency-Key").Should().BeTrue();
        LogRedaction.RedactHeader("Idempotency-Key", "key-1").Should().Be("[redacted]");
        LogRedaction.IsSensitiveHeader("X-Provider-Signature").Should().BeTrue();
        LogRedaction.RedactHeader("X-Provider-Signature", "v1=" + new string('a', 64)).Should().Be("[redacted]");
    }

    [Fact]
    public void Scope_does_not_include_payload_or_authorization()
    {
        RequestTrace trace = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "key-1",
            "/api/payment/request",
            "payment",
            "request");
        IReadOnlyDictionary<string, object> scope = ReliaPayLogging.ToScope(trace);
        string joined = string.Join(' ', scope.Select(pair => $"{pair.Key}={pair.Value}"));
        joined.Should().Contain("correlationId");
        joined.Should().Contain("requestId");
        joined.Should().Contain("module");
        joined.Should().Contain("action");
        joined.Should().NotContain("Bearer");
        joined.Should().NotContain("payload");
        joined.Should().NotContain("Authorization");
        scope.Values.OfType<string>().Should().NotContain(text => text.Contains("Bearer", StringComparison.Ordinal));
    }
}
