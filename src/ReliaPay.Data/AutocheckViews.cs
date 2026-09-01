namespace ReliaPay.Data;

/// <summary>
/// Имена read-only проекций <c>autocheck</c>. Физические таблицы могут называться иначе;
/// автопроверка смотрит только эти views.
/// </summary>
public static class AutocheckViews
{
    /// <summary>
    /// Версия machine-readable контракта. Ровно одна строка <c>course-1</c>.
    /// </summary>
    public const string ContractInfo = "contract_info";

    /// <summary>
    /// Опубликованные action без request/response schema, policy и checksum.
    /// </summary>
    public const string ActionDefinitions = "action_definitions";

    /// <summary>
    /// Журнал вызовов с <c>payload_hash</c>, без полного payload.
    /// </summary>
    public const string ActionDispatches = "action_dispatches";

    /// <summary>
    /// Предметные операции: сумма как numeric, без исходной строки amount.
    /// </summary>
    public const string Operations = "operations";

    /// <summary>
    /// Insert-only история событий операции.
    /// </summary>
    public const string OperationEvents = "operation_events";

    /// <summary>
    /// Опубликованные версии workflow-карт.
    /// </summary>
    public const string FlowVersions = "flow_versions";

    /// <summary>
    /// Экземпляры process с pinned flow version.
    /// </summary>
    public const string Processes = "processes";

    /// <summary>
    /// Step instances исполняемого process.
    /// </summary>
    public const string Steps = "steps";

    /// <summary>
    /// Jobs с lease и fencing.
    /// </summary>
    public const string Jobs = "jobs";

    /// <summary>
    /// Попытки исполнения job, включая STALE.
    /// </summary>
    public const string Attempts = "attempts";

    /// <summary>
    /// Идемпотентные сигналы process.
    /// </summary>
    public const string Signals = "signals";

    /// <summary>
    /// Append-only история workflow.
    /// </summary>
    public const string WorkflowEvents = "workflow_events";

    /// <summary>
    /// Проекции недели 1 в стабильном порядке ТЗ той недели.
    /// </summary>
    public static readonly IReadOnlyList<string> Week1 =
    [
        ContractInfo,
        ActionDefinitions,
        ActionDispatches,
        Operations,
        OperationEvents
    ];

    /// <summary>
    /// Полный набор проекций, включая workflow недели 2.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        ContractInfo,
        ActionDefinitions,
        ActionDispatches,
        Operations,
        OperationEvents,
        FlowVersions,
        Processes,
        Steps,
        Jobs,
        Attempts,
        Signals,
        WorkflowEvents
    ];
}
