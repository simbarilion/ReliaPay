using System.Text.Json;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.Execution;

/// <summary>
/// Результат разбора jsonb-конверта <c>api.invoke</c> без COMMIT.
/// </summary>
public abstract record ActionInvokeParseResult
{
    /// <summary>Успешный outcome и result, уже проверенные по манифесту.</summary>
    public sealed record Success(string Outcome, JsonElement Result) : ActionInvokeParseResult;

    /// <summary>Ошибка target или нарушение контракта; транзакцию откатывает вызывающий.</summary>
    public sealed record Failure(string Code, string Message, bool Retryable = false) : ActionInvokeParseResult;
}

/// <summary>
/// Проверяет конверт <c>api.invoke</c> так же, как HTTP-путь, но без работы с транзакцией.
/// Worker использует тот же разбор, чтобы не коммитить effect до <c>finish_job</c>.
/// </summary>
public static class ActionInvokeEnvelope
{
    /// <summary>
    /// Разбирает JSON <c>api.invoke</c> относительно опубликованного манифеста.
    /// </summary>
    public static ActionInvokeParseResult Parse(
        string invokeJson,
        ActionManifest manifest,
        JsonDocumentValidator validator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invokeJson);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(validator);

        using JsonDocument document = JsonDocument.Parse(invokeJson);
        JsonElement root = document.RootElement;
        string status = root.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            string code = root.TryGetProperty("code", out JsonElement codeElement)
                ? codeElement.GetString() ?? ErrorCodes.InternalError
                : ErrorCodes.InternalError;
            string message = root.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "action failed"
                : "action failed";
            bool retryable = root.TryGetProperty("retryable", out JsonElement retryableElement)
                && retryableElement.ValueKind == JsonValueKind.True;
            return new ActionInvokeParseResult.Failure(code, message, retryable);
        }

        if (!string.Equals(status, "ok", StringComparison.Ordinal)
            || !root.TryGetProperty("outcome", out JsonElement outcomeElement)
            || outcomeElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("result", out JsonElement resultElement)
            || resultElement.ValueKind != JsonValueKind.Object)
        {
            return ContractFailure();
        }

        string outcome = outcomeElement.GetString() ?? string.Empty;
        if (!manifest.Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            return ContractFailure();
        }

        SchemaValidationResult resultCheck = validator.Validate(
            resultElement.GetRawText(),
            manifest.ResponseSchema.GetRawText());
        if (!resultCheck.IsValid)
        {
            return ContractFailure();
        }

        return new ActionInvokeParseResult.Success(outcome, resultElement.Clone());
    }

    private static ActionInvokeParseResult.Failure ContractFailure()
    {
        return new ActionInvokeParseResult.Failure(
            ErrorCodes.ActionContractViolation,
            "action result does not match contract");
    }
}
