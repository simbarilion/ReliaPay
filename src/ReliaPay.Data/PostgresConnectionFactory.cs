using Npgsql;

namespace ReliaPay.Data;

/// <summary>
/// Собирает Npgsql-строки подключения для publication и runtime без печати секретов.
/// </summary>
public sealed class PostgresConnectionFactory
{
    private readonly PostgresEndpoints _endpoints;

    /// <summary>
    /// Создаёт фабрику для заданных endpoints.
    /// </summary>
    /// <param name="endpoints">Параметры хоста и ролей.</param>
    public PostgresConnectionFactory(PostgresEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = endpoints;
    }

    /// <summary>
    /// Открывает соединение роли publication. Вызывающая сторона обязана закрыть его.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Открытое соединение.</returns>
    public async Task<NpgsqlConnection> OpenPublicationConnectionAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(Build(PostgresRoles.Publication, _endpoints.PublicationUser, _endpoints.PublicationPassword));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Собирает строку подключения через <see cref="NpgsqlConnectionStringBuilder"/>, чтобы не конкатенировать пароль вручную.
    /// </summary>
    /// <param name="applicationName">Имя приложения в pg_stat_activity.</param>
    /// <param name="username">Роль PostgreSQL.</param>
    /// <param name="password">Пароль роли.</param>
    /// <returns>Строка подключения Npgsql.</returns>
    private string Build(string applicationName, string username, string password)
    {
        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = _endpoints.Host,
            Port = _endpoints.Port,
            Database = _endpoints.Database,
            Username = username,
            Password = password,
            ApplicationName = "reliapay-" + applicationName,
            Timeout = 15,
            CommandTimeout = 60,
            IncludeErrorDetail = false,
        };
        return builder.ConnectionString;
    }
}
