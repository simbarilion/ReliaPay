using FluentAssertions;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Json;

public sealed class JsonDocumentValidatorTests
{
    [Fact]
    public void Embedded_manifest_schema_accepts_minimal_probe()
    {
        JsonDocumentValidator validator = new();
        const string document = """
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
              "required_policy": [],
              "idempotency_mode": "none",
              "idempotency_scope": "none",
              "timeout_ms": 1
            }
            """;

        SchemaValidationResult result = validator.Validate(document, CourseJsonSchemas.ActionManifest);
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Broken_json_is_not_valid()
    {
        JsonDocumentValidator validator = new();
        SchemaValidationResult result = validator.Validate("{", "{\"type\":\"object\"}");
        result.IsValid.Should().BeFalse();
    }
}
