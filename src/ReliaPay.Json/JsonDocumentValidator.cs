using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace ReliaPay.Json;

/// <summary>
/// Результат проверки JSON-документа по schema манифеста.
/// </summary>
/// <param name="IsValid">Истина, если документ полностью соответствует schema.</param>
/// <param name="ErrorMessage">Краткое описание первого нарушения либо null при успехе.</param>
public sealed record SchemaValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>
/// Встроенные JSON Schema курса, которые CLI и runtime используют без чтения файлов с диска.
/// </summary>
public static class CourseJsonSchemas
{
    /// <summary>
    /// Каноническая schema action-манифеста <c>course-1</c>.
    /// </summary>
    public static string ActionManifest { get; } = Load("ReliaPay.action_manifest.schema.json");

    /// <summary>
    /// Читает embedded resource сборки.
    /// </summary>
    /// <param name="logicalName">LogicalName ресурса.</param>
    /// <returns>Текст schema.</returns>
    private static string Load(string logicalName)
    {
        Assembly assembly = typeof(CourseJsonSchemas).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded JSON schema '{logicalName}' is missing.");
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Проверяет JSON-документ по JSON Schema draft 2020-12.
/// </summary>
public sealed class JsonDocumentValidator
{
    /// <summary>
    /// Проверяет JSON-документ на соответствие переданной JSON Schema.
    /// </summary>
    /// <param name="documentJson">Сериализованный JSON документ.</param>
    /// <param name="schemaJson">Сериализованная JSON Schema.</param>
    /// <returns>Типизированный результат проверки.</returns>
    public SchemaValidationResult Validate(string documentJson, string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);

        JsonNode? documentNode;
        try
        {
            documentNode = JsonNode.Parse(documentJson);
        }
        catch (JsonException)
        {
            return new SchemaValidationResult(false, "JSON is invalid");
        }

        if (documentNode is null)
        {
            return new SchemaValidationResult(false, "JSON is invalid");
        }

        JsonSchema schema = JsonSchema.FromText(schemaJson);
        EvaluationResults results = schema.Evaluate(
            documentNode,
            new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        return results.IsValid
            ? new SchemaValidationResult(true, null)
            : new SchemaValidationResult(false, "document does not match schema");
    }
}
