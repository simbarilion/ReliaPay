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
}
