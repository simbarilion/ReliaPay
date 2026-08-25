using System.Text.Json;
using Npgsql;
using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.Catalog;

/// <summary>
/// Команды публикации и жизненного цикла action от имени роли publication.
/// </summary>
public sealed class ActionCatalogClient
{
    private readonly PostgresConnectionFactory _connectionFactory;

    /// <summary>
    /// Создаёт клиент поверх publication-соединений.
    /// </summary>
    /// <param name="connectionFactory">Фабрика CLI-роли.</param>
    public ActionCatalogClient(PostgresConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Публикует манифест через <c>api.publish_action</c>. Повтор того же тела безопасен.
    /// </summary>
    /// <param name="manifestJson">Исходный JSON файла.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Конверт каталога.</returns>
    public Task<CatalogMutationResult> PublishAsync(string manifestJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        return ExecuteMutationAsync(
            "SELECT api.publish_action(@manifest::jsonb)::text",
            command => command.Parameters.AddWithValue("manifest", manifestJson),
            cancellationToken);
    }

    /// <summary>
    /// Атомарно включает версию и делает её default.
    /// </summary>
    /// <param name="route">Ключ <c>module.action</c>.</param>
    /// <param name="version">Версия манифеста.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Конверт каталога.</returns>
    public Task<CatalogMutationResult> ActivateAsync(string route, int version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return ExecuteMutationAsync(
            "SELECT api.activate_action(@route, @version)::text",
            command =>
            {
                command.Parameters.AddWithValue("route", route);
                command.Parameters.AddWithValue("version", version);
            },
            cancellationToken);
    }

    /// <summary>
    /// Выключает версию; при необходимости назначает replacement default.
    /// </summary>
    /// <param name="route">Ключ <c>module.action</c>.</param>
    /// <param name="version">Выключаемая версия.</param>
    /// <param name="replacementVersion">Новая default, если без неё маршрут останется без default.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Конверт каталога.</returns>
    public Task<CatalogMutationResult> DisableAsync(
        string route,
        int version,
        int? replacementVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return ExecuteMutationAsync(
            "SELECT api.disable_action(@route, @version, @replacement)::text",
            command =>
            {
                command.Parameters.AddWithValue("route", route);
                command.Parameters.AddWithValue("version", version);
                command.Parameters.AddWithValue("replacement", replacementVersion.HasValue ? replacementVersion.Value : DBNull.Value);
            },
            cancellationToken);
    }

    /// <summary>
    /// Читает опубликованные версии в стабильном порядке.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Строки каталога.</returns>
    public async Task<IReadOnlyList<ActionListEntry>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                """
                SELECT module, action, version, enabled, is_default
                FROM api.actions
                ORDER BY module, action, version
                """,
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            List<ActionListEntry> entries = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(
                    new ActionListEntry(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetBoolean(3),
                        reader.GetBoolean(4)));
            }

            return entries;
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>
    /// Вызывает SECURITY DEFINER-функцию и разбирает jsonb-конверт.
    /// </summary>
    private async Task<CatalogMutationResult> ExecuteMutationAsync(
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connectionFactory
                .OpenPublicationConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(sql, connection);
            bind(command);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            string json = scalar as string ?? Convert.ToString(scalar) ?? "{}";
            return ParseMutation(json);
        }
        catch (NpgsqlException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>
    /// Разбирает конверт <c>api.publish_action</c> / activate / disable.
    /// </summary>
    /// <param name="json">JSON-текст из PostgreSQL.</param>
    /// <returns>Типизированный результат.</returns>
    private static CatalogMutationResult ParseMutation(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string status = root.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? "error"
            : "error";
        if (string.Equals(status, "ok", StringComparison.Ordinal))
        {
            int? version = null;
            if (root.TryGetProperty("version", out JsonElement versionElement)
                && versionElement.ValueKind == JsonValueKind.Number)
            {
                version = versionElement.GetInt32();
            }

            return new CatalogMutationResult(
                true,
                null,
                null,
                root.GetProperty("resource").GetString() ?? "action",
                root.GetProperty("operation").GetString() ?? string.Empty,
                root.GetProperty("key").GetString() ?? string.Empty,
                version);
        }

        return new CatalogMutationResult(
            false,
            root.TryGetProperty("code", out JsonElement code) ? code.GetString() : ErrorCodes.InternalError,
            root.TryGetProperty("message", out JsonElement message) ? message.GetString() : "catalog command failed",
            string.Empty,
            string.Empty,
            string.Empty,
            null);
    }

    /// <summary>
    /// Отделяет недоступность PostgreSQL от ошибки SQL без текста сервера в CLI.
    /// </summary>
    private static Exception Translate(NpgsqlException exception)
    {
        if (exception is PostgresException)
        {
            return new InvalidOperationException("catalog command failed");
        }

        return new PostgresUnavailableException();
    }
}
