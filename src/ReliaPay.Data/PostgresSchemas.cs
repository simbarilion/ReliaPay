namespace ReliaPay.Data;

/// <summary>
/// Имена схем PostgreSQL, которые образуют границу задания.
/// </summary>
public static class PostgresSchemas
{
    /// <summary>
    /// Каталог, <c>api.invoke</c> и журнал checksum-миграций.
    /// </summary>
    public const string Api = "api";

    /// <summary>
    /// Предметные таблицы operations/events. Runtime не получает прямой DML.
    /// </summary>
    public const string App = "app";

    /// <summary>
    /// Read-only views для автопроверки.
    /// </summary>
    public const string Autocheck = "autocheck";

    /// <summary>
    /// Определения карт, process/job/attempt и SECURITY DEFINER-границы worker.
    /// </summary>
    public const string Workflow = "workflow";

    /// <summary>
    /// Схемы учебной модели в стабильном порядке.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Api,
        App,
        Autocheck,
        Workflow
    ];
}
