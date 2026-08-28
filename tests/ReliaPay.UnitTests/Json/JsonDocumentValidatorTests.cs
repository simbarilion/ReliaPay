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
              "contract_version": "course-1",
              "module": "catalog",
              "action": "probe",
              "version": 1,
              "http_method": "POST",
              "target_schema": "app",
              "target_function": "catalog_probe",
              "request_schema": true,
              "response_schema": { "type": "object", "$defs": { "ok": { "type": "boolean" } } },
              "outcomes": ["PROBED"],
              "required_policy": [],
              "idempotency_mode": "optional",
              "idempotency_scope": "principal_action",
              "timeout_ms": 1,
              "enabled": false,
              "is_default": false,
              "x-extra": 1
            }
            """;

        SchemaValidationResult result = validator.Validate(document, CourseJsonSchemas.ActionManifest);
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Timeout_above_course_limit_is_rejected()
    {
        JsonDocumentValidator validator = new();
        const string document = """
            {
              "contract_version": "course-1",
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
              "timeout_ms": 30001,
              "enabled": false,
              "is_default": false
            }
            """;

        SchemaValidationResult result = validator.Validate(document, CourseJsonSchemas.ActionManifest);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Broken_json_is_not_valid()
    {
        JsonDocumentValidator validator = new();
        SchemaValidationResult result = validator.Validate("{", "{\"type\":\"object\"}");
        result.IsValid.Should().BeFalse();
    }
}
