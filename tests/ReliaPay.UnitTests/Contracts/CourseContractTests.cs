using System.Globalization;
using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Security;

namespace ReliaPay.UnitTests.Contracts;

public sealed class CourseContractTests
{
    [Fact]
    public void Contract_version_is_course_1()
    {
        CourseContracts.Version.Should().Be("course-1");
    }

    [Fact]
    public void Error_codes_match_the_course_matrix()
    {
        ErrorCodes.AuthInvalid.Should().Be("auth.invalid");
        ErrorCodes.SignatureInvalid.Should().Be("signature.invalid");
        ErrorCodes.ReceiptSignatureRequired.Should().Be("receipt.signature_required");
        ErrorCodes.ReceiptExternalRequestNotFound.Should().Be("receipt.external_request_not_found");
        ErrorCodes.RequestInvalid.Should().Be("request.invalid");
        ErrorCodes.IdempotencyRequired.Should().Be("idempotency.required");
        ErrorCodes.AccessDenied.Should().Be("access.denied");
        ErrorCodes.ActionNotFound.Should().Be("action.not_found");
        ErrorCodes.IdempotencyConflict.Should().Be("idempotency.conflict");
        ErrorCodes.PayloadInvalid.Should().Be("payload.invalid");
        ErrorCodes.DependencyUnavailable.Should().Be("dependency.unavailable");
        ErrorCodes.ActionContractViolation.Should().Be("action.contract_violation");
        ErrorCodes.ActionTimeout.Should().Be("action.timeout");
        ErrorCodes.InternalError.Should().Be("internal.error");
        ErrorCodes.ManifestInvalid.Should().Be("manifest.invalid");
        ErrorCodes.ManifestConflict.Should().Be("manifest.conflict");
        ErrorCodes.OperationNotFound.Should().Be("operation.not_found");
        ErrorCodes.MigrationConflict.Should().Be("migration.conflict");
        ErrorCodes.WorkflowMappingMissing.Should().Be("workflow.mapping_missing");
        ErrorCodes.WorkflowLeaseStale.Should().Be("workflow.lease_stale");
        ErrorCodes.WorkflowConflict.Should().Be("workflow.conflict");
    }

    [Fact]
    public void Action_catalog_key_formats_route_without_version()
    {
        ActionCatalogKey key = new("payment", "request", 1);
        key.RouteKey.Should().Be("payment.request");
    }

    [Fact]
    public void Trusted_context_keeps_principal_scopes_and_server_correlation()
    {
        Guid correlationId = Guid.Parse("1e534bdb-73a8-446a-a8f5-49c4959786c2");
        TrustedContext context = new(
            Principal: "candidate-client",
            Consumer: "web",
            Scopes: ["payment:write", "payment:read"],
            CorrelationId: correlationId,
            RequestId: "request-123",
            Deadline: DateTimeOffset.Parse("2026-08-28T12:00:02Z", CultureInfo.InvariantCulture));

        context.Principal.Should().Be("candidate-client");
        context.Scopes.Should().Equal("payment:write", "payment:read");
        context.CorrelationId.Should().Be(correlationId);
        context.RequestId.Should().Be("request-123");
        context.ExecutionId.Should().BeNull();
        context.ProcessId.Should().BeNull();
        context.JobId.Should().BeNull();
        context.AttemptId.Should().BeNull();
        context.Transport.Should().BeNull();
    }

    [Fact]
    public void Trusted_context_transport_keeps_only_verified_markers()
    {
        TransportMarkers transport = new(SignatureVerified: true, SignatureVersion: 1);
        TrustedContext context = new(
            Principal: "receipt-provider",
            Consumer: "provider",
            Scopes: ["receipt:write"],
            CorrelationId: Guid.Parse("1e534bdb-73a8-446a-a8f5-49c4959786c2"),
            RequestId: "provider-123",
            Deadline: DateTimeOffset.Parse("2026-09-09T12:00:02Z", CultureInfo.InvariantCulture),
            Transport: transport);

        context.Transport.Should().NotBeNull();
        context.Transport!.SignatureVerified.Should().BeTrue();
        context.Transport.SignatureVersion.Should().Be(1);
    }

    [Fact]
    public void Http_limits_keep_gateway_budget_above_manifest_timeout()
    {
        CourseHttpLimits.MaxTimeoutMs.Should().Be(30_000);
        CourseHttpLimits.GatewayHttpTimeoutSeconds.Should().BeGreaterThan(CourseHttpLimits.MaxTimeoutMs / 1000);
        CourseHttpLimits.MaxRequestBodyBytes.Should().Be(64 * 1024);
    }
}
