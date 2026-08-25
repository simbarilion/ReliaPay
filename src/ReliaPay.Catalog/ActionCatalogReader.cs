using System.Text.Json;
using Npgsql;
using ReliaPay.Data;

namespace ReliaPay.Catalog;

/// <summary>
/// Чтение опубликованного каталога ролью runtime. DML в catalog этой ролью запрещён.
/// </summary>
public sealed class ActionCatalogReader
{
    private readonly PostgresConnectionFactory _connectionFactory;

    /// <summary>
    /// Создаёт читателя поверх runtime-соединений.
    /// </summary>
    /// <param name="connectionFactory">Фабрика роли <c>course_runtime</c>.</param>
    public ActionCatalogReader(PostgresConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Резолвит explicit версию или единственную включённую default.
    /// </summary>
    /// <param name="module">Сегмент модуля.</param>
    /// <param name="action">Сегмент действия.</param>
    /// <param name="version">Явная версия либо null.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест либо null, если action недоступен.</returns>
    public async Task<ActionManifest?> ResolveAsync(
        string module,
        string action,
        int? version,
        CancellationToken cancellationToken)
    {
        const string explicitSql = """
            SELECT module, action, version, http_method, target_schema, target_function,
                   request_schema::text, response_schema::text, outcomes::text, required_policy::text,
                   idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default
            FROM api.actions
            WHERE module = @module AND action = @action AND version = @version AND enabled
            """;
        const string defaultSql = """
            SELECT module, action, version, http_method, target_schema, target_function,
                   request_schema::text, response_schema::text, outcomes::text, required_policy::text,
                   idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default
            FROM api.actions
            WHERE module = @module AND action = @action AND enabled AND is_default
            """;
        return await QuerySingleAsync(
                version is null ? defaultSql : explicitSql,
                command =>
                {
                    command.Parameters.AddWithValue("module", module);
                    command.Parameters.AddWithValue("action", action);
                    if (version is not null)
                    {
                        command.Parameters.AddWithValue("version", version.Value);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Возвращает опубликованную версию независимо от enabled — для точечного OpenAPI.
    /// </summary>
    /// <param name="module">Сегмент модуля.</param>
    /// <param name="action">Сегмент действия.</param>
    /// <param name="version">Точная версия.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест либо null, если версия не опубликована.</returns>
    public Task<ActionManifest?> GetPublishedAsync(
        string module,
        string action,
        int version,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT module, action, version, http_method, target_schema, target_function,
                   request_schema::text, response_schema::text, outcomes::text, required_policy::text,
                   idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default
            FROM api.actions
            WHERE module = @module AND action = @action AND version = @version
            """;
        return QuerySingleAsync(
            sql,
            command =>
            {
                command.Parameters.AddWithValue("module", module);
                command.Parameters.AddWithValue("action", action);
                command.Parameters.AddWithValue("version", version);
            },
            cancellationToken);
    }

    /// <summary>
    /// Включённые default-маршруты для <c>/openapi/default.json</c>.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Список default-манифестов.</returns>
    public async Task<IReadOnlyList<ActionManifest>> ListDefaultsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT module, action, version, http_method, target_schema, target_function,
                   request_schema::text, response_schema::text, outcomes::text, required_policy::text,
                   idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default
            FROM api.actions
            WHERE enabled AND is_default
            ORDER BY module, action
            """;
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenRuntimeConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            List<ActionManifest> items = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadManifest(reader));
            }

            return items;
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>
    /// Проверяет доступность PostgreSQL и завершение инициализации по <c>autocheck.contract_info</c>.
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenRuntimeConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                "SELECT contract_version FROM autocheck.contract_info LIMIT 1",
                connection);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is string version && string.Equals(version, "course-1", StringComparison.Ordinal);
        }
        catch (NpgsqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ActionManifest?> QuerySingleAsync(
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenRuntimeConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            bind(command);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadManifest(reader);
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    private static ActionManifest ReadManifest(NpgsqlDataReader reader)
    {
        return new ActionManifest(
            Module: reader.GetString(0),
            Action: reader.GetString(1),
            Version: reader.GetInt32(2),
            HttpMethod: reader.GetString(3),
            TargetSchema: reader.GetString(4),
            TargetFunction: reader.GetString(5),
            RequestSchema: JsonDocument.Parse(reader.GetString(6)).RootElement.Clone(),
            ResponseSchema: JsonDocument.Parse(reader.GetString(7)).RootElement.Clone(),
            Outcomes: ParseStringArray(reader.GetString(8)),
            RequiredPolicy: ParseStringArray(reader.GetString(9)),
            IdempotencyMode: reader.GetString(10),
            IdempotencyScope: reader.GetString(11),
            TimeoutMs: reader.GetInt32(12),
            Enabled: reader.GetBoolean(13),
            IsDefault: reader.GetBoolean(14));
    }

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<string> values = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static Exception Translate(NpgsqlException exception)
    {
        if (exception is PostgresException)
        {
            return new InvalidOperationException("catalog read failed");
        }

        return new PostgresUnavailableException();
    }
}
