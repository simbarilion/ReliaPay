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
    /// Полный набор проекций недели 1 в стабильном порядке ТЗ.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        ContractInfo,
        ActionDefinitions,
        ActionDispatches,
        Operations,
        OperationEvents
    ];
}
