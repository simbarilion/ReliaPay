using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Catalog;

public sealed class ActionManifestReaderTests
{
    private static readonly ActionManifestReader Reader = new(new JsonDocumentValidator());

    [Fact]
    public void Valid_manifest_maps_route_and_target()
    {
        const string json = """
            {
              "module": "catalog",
              "action": "probe",
              "version": 1,
              "http_method": "POST",
              "target_schema": "app",
              "target_function": "catalog_probe",
              "request_schema": { "type": "object" },
              "response_schema": { "type": "object" },
              "outcomes": ["PROBED"],
              "required_policy": ["catalog:probe"],
              "idempotency_mode": "none",
              "idempotency_scope": "none",
              "timeout_ms": 1000
            }
            """;

        ActionManifestReadResult result = Reader.Read(json);
        result.IsSuccess.Should().BeTrue();
        result.Manifest!.RouteKey.Should().Be("catalog.probe");
        result.Manifest.TargetFunction.Should().Be("catalog_probe");
        result.Manifest.Enabled.Should().BeFalse();
        result.Manifest.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Extra_property_is_manifest_invalid()
    {
        const string json = """
            {
              "module": "catalog",
              "action": "probe",
              "version": 1,
              "http_method": "POST",
              "target_schema": "app",
              "target_function": "catalog_probe",
              "request_schema": { "type": "object" },
              "response_schema": { "type": "object" },
              "outcomes": ["PROBED"],
              "required_policy": ["catalog:probe"],
              "idempotency_mode": "none",
              "idempotency_scope": "none",
              "timeout_ms": 1000,
              "enabled": true
            }
            """;

        ActionManifestReadResult result = Reader.Read(json);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ManifestInvalid);
    }

    [Fact]
    public void Get_method_is_rejected()
    {
        const string json = """
            {
              "module": "catalog",
              "action": "probe",
              "version": 1,
              "http_method": "GET",
              "target_schema": "app",
              "target_function": "catalog_probe",
              "request_schema": { "type": "object" },
              "response_schema": { "type": "object" },
              "outcomes": ["PROBED"],
              "required_policy": ["catalog:probe"],
              "idempotency_mode": "none",
              "idempotency_scope": "none",
              "timeout_ms": 1000
            }
            """;

        Reader.Read(json).ErrorCode.Should().Be(ErrorCodes.ManifestInvalid);
    }
}
