using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.Catalog;

/// <summary>
/// Результат чтения файла манифеста без обращения к PostgreSQL.
/// </summary>
/// <param name="Manifest">Типизированный манифест при успехе.</param>
/// <param name="RawJson">Исходный текст файла; его же отправляют в <c>api.publish_action</c>.</param>
/// <param name="ErrorCode">Код ошибки CLI при неуспехе.</param>
/// <param name="ErrorMessage">Сообщение без содержимого секретов.</param>
public sealed record ActionManifestReadResult(
    ActionManifest? Manifest,
    string? RawJson,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Успешный разбор.</summary>
    public bool IsSuccess => Manifest is not null && RawJson is not null;
}

/// <summary>
/// Читает JSON-манифест, проверяет schema курса и собирает типизированную модель.
/// </summary>
public sealed class ActionManifestReader
{
    private readonly JsonDocumentValidator _validator;

    /// <summary>
    /// Создаёт читателя с валидатором JSON Schema.
    /// </summary>
    /// <param name="validator">Проверка документа по schema манифеста.</param>
    public ActionManifestReader(JsonDocumentValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    /// <summary>
    /// Разбирает JSON-текст манифеста.
    /// </summary>
    /// <param name="json">Содержимое файла.</param>
    /// <returns>Манифест либо код <c>manifest.invalid</c>.</returns>
    public ActionManifestReadResult Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SchemaValidationResult validation = _validator.Validate(json, CourseJsonSchemas.ActionManifest);
        if (!validation.IsValid)
        {
            return new ActionManifestReadResult(
                null,
                null,
                ErrorCodes.ManifestInvalid,
                validation.ErrorMessage ?? "manifest does not match schema");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            ActionManifest manifest = new(
                Module: root.GetProperty("module").GetString() ?? string.Empty,
                Action: root.GetProperty("action").GetString() ?? string.Empty,
                Version: root.GetProperty("version").GetInt32(),
                HttpMethod: root.GetProperty("http_method").GetString() ?? string.Empty,
                TargetSchema: root.GetProperty("target_schema").GetString() ?? string.Empty,
                TargetFunction: root.GetProperty("target_function").GetString() ?? string.Empty,
                RequestSchema: root.GetProperty("request_schema").Clone(),
                ResponseSchema: root.GetProperty("response_schema").Clone(),
                Outcomes: ReadStringArray(root.GetProperty("outcomes")),
                RequiredPolicy: ReadStringArray(root.GetProperty("required_policy")),
                IdempotencyMode: root.GetProperty("idempotency_mode").GetString() ?? string.Empty,
                IdempotencyScope: root.GetProperty("idempotency_scope").GetString() ?? string.Empty,
                TimeoutMs: root.GetProperty("timeout_ms").GetInt32(),
                Enabled: false,
                IsDefault: false);
            return new ActionManifestReadResult(manifest, json, null, null);
        }
        catch (JsonException)
        {
            return new ActionManifestReadResult(null, null, ErrorCodes.ManifestInvalid, "manifest is not valid JSON");
        }
    }

    /// <summary>
    /// Читает JSON-массив строк.
    /// </summary>
    /// <param name="element">Элемент массива.</param>
    /// <returns>Список строк.</returns>
    private static IReadOnlyList<string> ReadStringArray(JsonElement element)
    {
        List<string> values = [];
        foreach (JsonElement item in element.EnumerateArray())
        {
            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }
}
