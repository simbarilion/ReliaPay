using System.Text.Json;
using ReliaPay.Catalog;
using ReliaPay.Security;

namespace ReliaPay.Worker;

/// <summary>
/// Job из <c>workflow.claim_jobs</c>: манифест и mapping уже в payload, каталог не читается.
/// </summary>
public sealed class ClaimedJob
{
    /// <summary>Идентификатор job.</summary>
    public required Guid JobId { get; init; }

    /// <summary>Процесс, которому принадлежит job.</summary>
    public required Guid ProcessId { get; init; }

    /// <summary>Экземпляр шага.</summary>
    public required Guid StepInstanceId { get; init; }

    /// <summary>Идентификатор исполнения; это <c>requestId</c> для invoke.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Идентификатор текущей попытки.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>Владелец текущего lease.</summary>
    public required string LeaseOwner { get; init; }

    /// <summary>Монотонная версия lease для fencing.</summary>
    public required long LeaseVersion { get; init; }

    /// <summary>Модуль опубликованного action.</summary>
    public required string Module { get; init; }

    /// <summary>Имя опубликованного action.</summary>
    public required string Action { get; init; }

    /// <summary>Закреплённая версия action.</summary>
    public required int ActionVersion { get; init; }

    /// <summary>JSON Schema входа из шага карты.</summary>
    public required JsonElement RequestSchema { get; init; }

    /// <summary>JSON Schema result из шага карты.</summary>
    public required JsonElement ResponseSchema { get; init; }

    /// <summary>Допустимые outcome.</summary>
    public required IReadOnlyList<string> Outcomes { get; init; }

    /// <summary>Scopes, которые worker ставит в context.</summary>
    public required IReadOnlyList<string> RequiredPolicy { get; init; }

    /// <summary>Лимит исполнения шага в миллисекундах.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>Назначение JSON Pointer → источник в process data.</summary>
    public required IReadOnlyDictionary<string, string> InputMapping { get; init; }

    /// <summary>Константы шага.</summary>
    public required JsonElement InputConstants { get; init; }

    /// <summary>Текущие process data.</summary>
    public required JsonElement ProcessData { get; init; }

    /// <summary>
    /// Собирает манифест для валидации конверта invoke без SELECT в каталог.
    /// </summary>
    public ActionManifest ToManifest()
    {
        return new ActionManifest(
            Module,
            Action,
            ActionVersion,
            "POST",
            "app",
            "unused",
            RequestSchema,
            ResponseSchema,
            Outcomes,
            RequiredPolicy,
            "none",
            "none",
            TimeoutMs,
            true,
            true);
    }

    /// <summary>
    /// Доверенный контекст worker: principal фиксирован, requestId равен executionId.
    /// </summary>
    /// <param name="correlationId">Новый UUID следа этой попытки.</param>
    /// <param name="now">Момент начала попытки.</param>
    public TrustedContext ToTrustedContext(Guid correlationId, DateTimeOffset now)
    {
        return new TrustedContext(
            WorkerIdentity.Principal,
            WorkerIdentity.Consumer,
            RequiredPolicy,
            correlationId,
            ExecutionId.ToString("D"),
            now.AddMilliseconds(TimeoutMs),
            ExecutionId.ToString("D"),
            FormatId(ProcessId),
            FormatId(JobId),
            FormatId(AttemptId));
    }

    private static string? FormatId(Guid value)
    {
        return value == Guid.Empty ? null : value.ToString("D");
    }
}

/// <summary>
/// Результат разбора конверта <c>claim_jobs</c>.
/// </summary>
/// <param name="Jobs">Захваченные job; пусто при idle.</param>
/// <param name="ErrorCode">Код ошибки конверта либо null.</param>
public sealed record ClaimParseResult(IReadOnlyList<ClaimedJob> Jobs, string? ErrorCode);

/// <summary>
/// Разбирает jsonb <c>workflow.claim_jobs</c>.
/// </summary>
public static class ClaimedJobParser
{
    /// <summary>
    /// Читает idle, список job или error-конверт.
    /// </summary>
    /// <param name="json">Текст jsonb-ответа.</param>
    public static ClaimParseResult Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string status = ReadString(root, "status") ?? string.Empty;
        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            return new ClaimParseResult([], ReadString(root, "code"));
        }

        if (!root.TryGetProperty("jobs", out JsonElement jobsElement)
            || jobsElement.ValueKind != JsonValueKind.Array)
        {
            return new ClaimParseResult([], ReliaPay.Contracts.ErrorCodes.InternalError);
        }

        List<ClaimedJob> jobs = [];
        foreach (JsonElement item in jobsElement.EnumerateArray())
        {
            jobs.Add(ParseJob(item));
        }

        return new ClaimParseResult(jobs, null);
    }

    private static ClaimedJob ParseJob(JsonElement item)
    {
        JsonElement constants = CloneOrEmpty(item, "inputConstants");
        JsonElement processData = CloneOrEmpty(item, "processData");
        JsonElement requestSchema = CloneOrObjectSchema(item, "requestSchema");
        JsonElement responseSchema = CloneOrObjectSchema(item, "responseSchema");

        return new ClaimedJob
        {
            JobId = ReadGuid(item, "jobId"),
            ProcessId = ReadGuid(item, "processId"),
            StepInstanceId = ReadGuid(item, "stepInstanceId"),
            ExecutionId = ReadGuid(item, "executionId"),
            AttemptId = ReadGuid(item, "attemptId"),
            LeaseOwner = ReadString(item, "leaseOwner") ?? string.Empty,
            LeaseVersion = ReadInt64(item, "leaseVersion"),
            Module = ReadString(item, "module") ?? string.Empty,
            Action = ReadString(item, "action") ?? string.Empty,
            ActionVersion = ReadInt32(item, "actionVersion"),
            RequestSchema = requestSchema,
            ResponseSchema = responseSchema,
            Outcomes = ReadStringArray(item, "outcomes"),
            RequiredPolicy = ReadStringArray(item, "requiredPolicy"),
            TimeoutMs = Math.Max(1, ReadInt32(item, "timeoutMs")),
            InputMapping = ReadStringMap(item, "inputMapping"),
            InputConstants = constants,
            ProcessData = processData,
        };
    }

    private static Guid ReadGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return Guid.Empty;
        }

        if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid parsed))
        {
            return parsed;
        }

        return value.TryGetGuid(out Guid guid) ? guid : Guid.Empty;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.TryGetInt64(out long number) ? number : value.GetInt32();
    }

    private static int ReadInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return value.GetInt32();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<string> items = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string text)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, string name)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is string source)
            {
                map[property.Name] = source;
            }
        }

        return map;
    }

    private static JsonElement CloneOrEmpty(JsonElement item, string name)
    {
        if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return value.Clone();
        }

        using JsonDocument empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static JsonElement CloneOrObjectSchema(JsonElement item, string name)
    {
        if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            return value.Clone();
        }

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        return schema.RootElement.Clone();
    }
}
