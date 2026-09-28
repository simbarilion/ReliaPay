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

    [Theory]
    [InlineData("X-JWT-Token")]
    [InlineData("X-HMAC-Secret")]
    public void Redacts_jwt_and_hmac_headers(string name)
    {
        LogRedaction.IsSensitiveHeader(name).Should().BeTrue();
        LogRedaction.RedactHeader(name, "value").Should().Be("[redacted]");
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("callback.message")]
    [InlineData("receipt.body")]
    [InlineData("provider.hmac")]
    public void Marks_forbidden_activity_tags(string tagName)
    {
        LogRedaction.IsSensitiveTag(tagName).Should().BeTrue();
    }

    [Fact]
    public void Job_scope_contains_workflow_ids_without_payload()
    {
        IReadOnlyDictionary<string, object> scope = ReliaPayLogging.ToJobScope(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            7,
            "payment",
            "prepare");
        scope.Should().ContainKey("jobId");
        scope.Should().ContainKey("processId");
        scope.Should().ContainKey("executionId");
        scope.Should().ContainKey("attemptId");
        scope.Should().ContainKey("leaseVersion");
        scope.Should().ContainKey("module");
        scope.Should().ContainKey("action");
        string joined = string.Join(' ', scope.Select(pair => $"{pair.Key}={pair.Value}"));
        joined.Should().NotContain("payload");
        joined.Should().NotContain("Bearer");
        joined.Should().NotContain("reason");
    }
}
