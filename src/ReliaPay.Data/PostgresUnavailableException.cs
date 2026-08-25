namespace ReliaPay.Data;

/// <summary>
/// PostgreSQL недоступен или соединение отклонено. Сообщение клиенту не содержит connection string.
/// </summary>
public sealed class PostgresUnavailableException : Exception
{
    /// <summary>
    /// Создаёт ошибку зависимости без утечки внутренней диагностики в текст исключения.
    /// </summary>
    public PostgresUnavailableException()
        : base("PostgreSQL is unavailable.")
    {
    }
}
