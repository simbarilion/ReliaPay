using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;
using ReliaPay.Json;
using ReliaPay.Observability;
using ReliaPay.Security;

namespace ReliaPay.Execution;

/// <summary>
/// Универсальный исполнитель POST /api/{module}/{action}: schema, транзакция, api.invoke, commit/rollback.
/// </summary>
public sealed class ActionExecutor
{
    private static readonly JsonSerializerOptions ContextJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ActionCatalogReader _catalog;
    private readonly PostgresConnectionFactory _connections;
    private readonly JsonDocumentValidator _validator;

    /// <summary>
    /// Создаёт исполнитель поверх catalog, runtime-соединений и JSON Schema.
    /// </summary>
    /// <param name="catalog">Чтение манифестов ролью runtime.</param>
    /// <param name="connections">Фабрика runtime-соединений.</param>
    /// <param name="validator">Проверка request/response schema.</param>
    public ActionExecutor(
        ActionCatalogReader catalog,
        PostgresConnectionFactory connections,
        JsonDocumentValidator validator)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(validator);
        _catalog = catalog;
        _connections = connections;
        _validator = validator;
    }

    /// <summary>
    /// Исполняет action после успешной аутентификации.
    /// </summary>
    /// <param name="route">Разобранный маршрут.</param>
    /// <param name="principal">Проверенный JWT.</param>
    /// <param name="idempotencyKey">Заголовок Idempotency-Key.</param>
    /// <param name="payloadJson">Сырое JSON-тело.</param>
    /// <param name="correlationId">Серверный UUID следа, общий с gateway.</param>
    /// <param name="cancellationToken">Токен HTTP-запроса.</param>
    /// <returns>Контрактный HTTP-результат.</returns>
    public async Task<ActionHttpResult> ExecuteAsync(
        ActionRoute route,
        JwtPrincipal principal,
        string? idempotencyKey,
        string payloadJson,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        using Activity? activity = ReliaPayInstrumentation.ActivitySource.StartActivity("api.action.execute");
        ReliaPayActivity.ApplyTags(
            activity,
            new RequestTrace(
                correlationId,
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
                $"/api/{route.Module}/{route.Action}",
                route.Module,
                route.Action));

        ActionManifest? manifest;
        try
        {
            manifest = await _catalog
                .ResolveAsync(route.Module, route.Action, route.ExplicitVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresUnavailableException)
        {
            return Fail(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable", correlationId, null);
        }
        catch (InvalidOperationException)
        {
            return Fail(ErrorCodes.InternalError, "action failed", correlationId, null);
        }

        if (manifest is null)
        {
            return Fail(ErrorCodes.ActionNotFound, "action is not available", correlationId, null);
        }

        activity?.SetTag("action.version", manifest.Version);

        if (!ActionPolicyChecker.IsSatisfied(manifest.RequiredPolicy, principal))
        {
            return Fail(ErrorCodes.AccessDenied, "required policy is not satisfied", correlationId, manifest.Version);
        }

        if (RequiresIdempotencyKey(manifest.IdempotencyMode)
            && string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Fail(ErrorCodes.IdempotencyRequired, "Idempotency-Key is required", correlationId, manifest.Version);
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return Fail(ErrorCodes.RequestInvalid, "request body must be JSON", correlationId, manifest.Version);
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(payloadJson);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(ErrorCodes.RequestInvalid, "request body must be a JSON object", correlationId, manifest.Version);
            }
        }
        catch (JsonException)
        {
            return Fail(ErrorCodes.RequestInvalid, "request body must be JSON", correlationId, manifest.Version);
        }

        SchemaValidationResult payloadCheck = _validator.Validate(payloadJson, manifest.RequestSchema.GetRawText());
        if (!payloadCheck.IsValid)
        {
            return Fail(ErrorCodes.PayloadInvalid, "payload does not match schema", correlationId, manifest.Version);
        }

        TrustedContext context = new(
            Principal: principal.Principal ?? string.Empty,
            Consumer: principal.Consumer ?? string.Empty,
            Scopes: principal.Scopes,
            CorrelationId: correlationId,
            RequestId: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            Deadline: DateTimeOffset.UtcNow.AddMilliseconds(manifest.TimeoutMs));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(manifest.TimeoutMs);
        return await InvokeInTransactionAsync(manifest, context, payloadJson, cancellationToken, timeout.Token)
            .ConfigureAwait(false);
    }

    private async Task<ActionHttpResult> InvokeInTransactionAsync(
        ActionManifest manifest,
        TrustedContext context,
        string payloadJson,
        CancellationToken httpCancellation,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await _connections.OpenRuntimeConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                "SELECT api.invoke(@module, @action, @version, @context::jsonb, @payload::jsonb)::text",
                connection,
                transaction);
            command.CommandTimeout = Math.Max(1, (manifest.TimeoutMs + 999) / 1000);
            command.Parameters.AddWithValue("module", manifest.Module);
            command.Parameters.AddWithValue("action", manifest.Action);
            command.Parameters.AddWithValue("version", manifest.Version);
            command.Parameters.AddWithValue("context", JsonSerializer.Serialize(ToInvokeContext(context), ContextJsonOptions));
            command.Parameters.AddWithValue("payload", payloadJson);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            string json = scalar as string ?? Convert.ToString(scalar) ?? "{}";
            return await CompleteInvokeAsync(manifest, context.CorrelationId, json, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!httpCancellation.IsCancellationRequested)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return Fail(ErrorCodes.ActionTimeout, "action timed out", context.CorrelationId, manifest.Version);
        }
        catch (PostgresException exception) when (exception.SqlState == "57014")
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return Fail(ErrorCodes.ActionTimeout, "action timed out", context.CorrelationId, manifest.Version);
        }
        catch (PostgresUnavailableException)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return Fail(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable", context.CorrelationId, manifest.Version);
        }
        catch (NpgsqlException exception)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            if (exception is PostgresException)
            {
                return Fail(ErrorCodes.InternalError, "action failed", context.CorrelationId, manifest.Version);
            }

            return Fail(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable", context.CorrelationId, manifest.Version);
        }
        catch (InvalidOperationException)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return Fail(ErrorCodes.InternalError, "action failed", context.CorrelationId, manifest.Version);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ActionHttpResult> CompleteInvokeAsync(
        ActionManifest manifest,
        Guid correlationId,
        string invokeJson,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(invokeJson);
        JsonElement root = document.RootElement;
        string status = root.TryGetProperty("status", out JsonElement statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.Equals(status, "error", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            string code = root.TryGetProperty("code", out JsonElement codeElement)
                ? codeElement.GetString() ?? ErrorCodes.InternalError
                : ErrorCodes.InternalError;
            string message = root.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "action failed"
                : "action failed";
            return Fail(code, SanitizeMessage(code, message), correlationId, manifest.Version);
        }

        if (!string.Equals(status, "ok", StringComparison.Ordinal)
            || !root.TryGetProperty("outcome", out JsonElement outcomeElement)
            || outcomeElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("result", out JsonElement resultElement)
            || resultElement.ValueKind != JsonValueKind.Object)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Fail(
                ErrorCodes.ActionContractViolation,
                "action result does not match contract",
                correlationId,
                manifest.Version);
        }

        string outcome = outcomeElement.GetString() ?? string.Empty;
        if (!manifest.Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Fail(
                ErrorCodes.ActionContractViolation,
                "action result does not match contract",
                correlationId,
                manifest.Version);
        }

        SchemaValidationResult resultCheck = _validator.Validate(
            resultElement.GetRawText(),
            manifest.ResponseSchema.GetRawText());
        if (!resultCheck.IsValid)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Fail(
                ErrorCodes.ActionContractViolation,
                "action result does not match contract",
                correlationId,
                manifest.Version);
        }

        JsonElement resultClone = resultElement.Clone();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActionHttpSuccess(
            new HttpSuccessEnvelope(
                "ok",
                outcome,
                resultClone,
                new HttpResponseMeta(correlationId, manifest.Version)));
    }

    private static bool RequiresIdempotencyKey(string mode)
    {
        return !string.Equals(mode, "none", StringComparison.Ordinal)
            && !string.Equals(mode, "optional", StringComparison.Ordinal);
    }

    private static InvokeContextDto ToInvokeContext(TrustedContext context)
    {
        return new InvokeContextDto(
            context.Principal,
            context.Consumer,
            context.Scopes,
            context.CorrelationId,
            context.RequestId,
            context.Deadline);
    }

    private static string SanitizeMessage(string code, string message)
    {
        if (code is ErrorCodes.InternalError or ErrorCodes.ActionContractViolation)
        {
            return code == ErrorCodes.ActionContractViolation
                ? "action result does not match contract"
                : "action failed";
        }

        return message;
    }

    private static ActionHttpFailure Fail(string code, string message, Guid? correlationId, int? version)
    {
        return new ActionHttpFailure(
            HttpErrorMatrix.StatusCode(code),
            HttpErrorEnvelope.Create(code, message, HttpErrorMatrix.Retryable(code), correlationId, version));
    }

    private static async Task TryRollbackAsync(NpgsqlTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private sealed record InvokeContextDto(
        string Principal,
        string Consumer,
        IReadOnlyList<string> Scopes,
        Guid CorrelationId,
        string? RequestId,
        DateTimeOffset Deadline);
}
