using System.Text.Json;

namespace ReliaPay.Catalog;

/// <summary>
/// Снимок опубликованного action для семантической проверки карты.
/// </summary>
public sealed record PublishedActionSnapshot(
    string Module,
    string Action,
    int Version,
    bool Enabled,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> RequiredPolicy);

/// <summary>
/// Каталог опубликованных action, доступный CLI-роли publication.
/// </summary>
public interface IPublishedActionLookup
{
    /// <summary>
    /// Ищет точную версию без фильтра enabled, чтобы отличить unknown от disabled.
    /// </summary>
    Task<PublishedActionSnapshot?> FindAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken);
}

/// <summary>
/// Типизированная workflow-карта после schema-проверки.
/// </summary>
public sealed record WorkflowMap(
    string FlowName,
    int Version,
    string StartStep,
    IReadOnlyList<WorkflowMapStep> Steps,
    IReadOnlyList<WorkflowMapTransition> Transitions,
    string CanonicalJson);

/// <summary>Шаг карты.</summary>
public sealed record WorkflowMapStep(
    string Key,
    string Type,
    WorkflowMapTask? Task,
    string? SignalType,
    string? Outcome,
    IReadOnlyList<string>? AllowedOutcomes);

/// <summary>Задача automatic-шага.</summary>
public sealed record WorkflowMapTask(
    string Module,
    string Action,
    int ActionVersion,
    IReadOnlyList<string> RequiredPolicy,
    int TimeoutMs,
    int MaxAttempts,
    IReadOnlyList<int> DelaysMs,
    IReadOnlyDictionary<string, string> InputMapping,
    JsonElement InputConstants);

/// <summary>Переход по outcome.</summary>
public sealed record WorkflowMapTransition(string From, string Outcome, string To);

/// <summary>
/// Результат чтения JSON/YAML карты без записи в PostgreSQL.
/// </summary>
public sealed record WorkflowMapReadResult(
    WorkflowMap? Map,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Успешный разбор и schema-проверка.</summary>
    public bool IsSuccess => Map is not null;
}
