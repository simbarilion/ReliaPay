namespace ReliaPay.UnitTests.Catalog;

/// <summary>
/// Каноническая форма course-1 манифеста для проверки schema: обязательные флаги
/// контракта плюс вложенный Draft 2020-12. Не уже опубликованного контракта.
/// </summary>
internal static class CourseManifestJson
{
    public static string OfficialProbe(
        string extraProperties = "",
        string idempotencyMode = "none",
        string httpMethod = "POST")
    {
        return $$"""
            {
              "contract_version": "course-1",
              "module": "catalog",
              "action": "probe",
              "version": 1,
              "http_method": "{{httpMethod}}",
              "target_schema": "app",
              "target_function": "catalog_probe",
              "request_schema": {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object",
                "properties": {
                  "token": { "type": "string" }
                }
              },
              "response_schema": {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "type": "object"
              },
              "outcomes": ["PROBED"],
              "required_policy": ["catalog:probe"],
              "idempotency_mode": "{{idempotencyMode}}",
              "idempotency_scope": "principal_action",
              "timeout_ms": 1000,
              "enabled": false,
              "is_default": false{{extraProperties}}
            }
            """;
    }
}
