namespace ReliaPay.Json;

/// <summary>
/// Результат проверки JSON-документа по schema манифеста.
/// </summary>
/// <param name="IsValid">Истина, если документ полностью соответствует schema.</param>
/// <param name="ErrorMessage">Краткое описание первого нарушения либо null при успехе.</param>
public sealed record SchemaValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>
/// Граница валидации request/response schema. Реализация JsonSchema.Net подключается
/// на этапе HTTP-исполнителя; каркас фиксирует типизированный контракт метода.
/// </summary>
public sealed class JsonDocumentValidator
{
    /// <summary>
    /// Проверяет JSON-документ на соответствие переданной JSON Schema.
    /// </summary>
    /// <param name="documentJson">Сериализованный JSON документ.</param>
    /// <param name="schemaJson">Сериализованная JSON Schema.</param>
    /// <returns>Типизированный результат проверки.</returns>
    /// <exception cref="NotSupportedException">В каркасе решения валидатор ещё не подключён.</exception>
    public SchemaValidationResult Validate(string documentJson, string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        throw new NotSupportedException("JSON Schema validation is not wired in the solution skeleton.");
    }
}
