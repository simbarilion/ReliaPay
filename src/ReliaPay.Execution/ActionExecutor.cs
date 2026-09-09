using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Универсальный исполнитель POST /api/{module}/{action}. HTTP открывает runtime-транзакцию
/// и коммитит сам; worker передаёт свою транзакцию в <see cref="InvokeOnConnectionAsync"/>.
/// </summary>
public sealed class ActionExecutor
{
    private static readonly JsonSerializerOptions ContextJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ActionCatalogReader? _catalog;
    private readonly PostgresConnectionFactory _connections;
    private readonly JsonDocumentValidator _validator;
    private readonly ProviderHmacOptions _hmac;

    /// <summary>
    /// Создаёт исполнитель поверх catalog, runtime-соединений, JSON Schema и HMAC-ключа.
    /// </summary>
    /// <param name="catalog">Чтение манифестов ролью runtime.</param>
    /// <param name="connections">Фабрика runtime-соединений.</param>
    /// <param name="validator">Проверка request/response schema.</param>
    /// <param name="hmac">Raw UTF-8 ключ <c>PROVIDER_HMAC_SECRET</c>.</param>
    public ActionExecutor(
        ActionCatalogReader catalog,
        PostgresConnectionFactory connections,
        JsonDocumentValidator validator,
        ProviderHmacOptions hmac)
        : this(connections, validator, catalog, hmac)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hmac);
    }

    /// <summary>
    /// Исполнитель для worker: только <see cref="InvokeOnConnectionAsync"/> без чтения каталога.
    /// </summary>
    /// <param name="connections">Фабрика соединений роли worker.</param>
    /// <param name="validator">Проверка request/response schema из claim.</param>
    public ActionExecutor(
        PostgresConnectionFactory connections,
        JsonDocumentValidator validator)
        : this(connections, validator, catalog: null, hmac: new ProviderHmacOptions([]))
    {
    }

    private ActionExecutor(
        PostgresConnectionFactory connections,
        JsonDocumentValidator validator,
        ActionCatalogReader? catalog,
        ProviderHmacOptions hmac)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(hmac);
        _catalog = catalog;
        _connections = connections;
        _validator = validator;
        _hmac = hmac;
    }

    /// <summary>
    /// Исполняет action после успешной аутентификации.
    /// </summary>
    /// <param name="route">Разобранный маршрут.</param>
    /// <param name="principal">Проверенный JWT.</param>
    /// <param name="idempotencyKey">Заголовок Idempotency-Key.</param>
    /// <param name="payloadBytes">Исходные байты HTTP-тела; HMAC считается до JSON-parse.</param>
    /// <param name="providerSignature">Заголовок <c>X-Provider-Signature</c>, если передан.</param>
    /// <param name="correlationId">Серверный UUID следа, общий с gateway.</param>
    /// <param name="cancellationToken">Токен HTTP-запроса.</param>
    /// <returns>Контрактный HTTP-результат.</returns>
    public async Task<ActionHttpResult> ExecuteAsync(
        ActionRoute route,
        JwtPrincipal principal,
        string? idempotencyKey,
        byte[] payloadBytes,
        string? providerSignature,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);
        using Activity? activity = ReliaPayInstrumentation.ActivitySource.StartActivity("api.action.execute");
        ReliaPayActivity.ApplyTags(
            activity,
            new RequestTrace(
                correlationId,
                RequestIdHash.Sha256Hex(idempotencyKey),
                $"/api/{route.Module}/{route.Action}",
                route.Module,
                route.Action));

        if (_catalog is null)
        {
            throw new InvalidOperationException("HTTP execution requires an action catalog.");
        }

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

        ProviderHmacResult hmacResult = ProviderHmac.Verify(_hmac.Key, payloadBytes, providerSignature);
        if (hmacResult == ProviderHmacResult.Invalid)
        {
            return Fail(ErrorCodes.SignatureInvalid, "provider signature is invalid", correlationId, manifest.Version);
        }

        if (payloadBytes.Length == 0)
        {
            return Fail(ErrorCodes.RequestInvalid, "request body must be JSON", correlationId, manifest.Version);
        }

        string payloadJson = Encoding.UTF8.GetString(payloadBytes);
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

        TransportMarkers? transport = hmacResult == ProviderHmacResult.Verified
            ? new TransportMarkers(SignatureVerified: true, SignatureVersion: 1)
            : null;

        TrustedContext context = new(
            Principal: principal.Principal ?? string.Empty,
            Consumer: principal.Consumer ?? string.Empty,
            Scopes: principal.Scopes,
            CorrelationId: correlationId,
            RequestId: string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(),
            Deadline: DateTimeOffset.UtcNow.AddMilliseconds(manifest.TimeoutMs),
            Transport: transport);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(manifest.TimeoutMs);
        return await InvokeInTransactionAsync(manifest, context, payloadJson, cancellationToken, timeout.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Вызывает <c>api.invoke</c> на уже открытой транзакции и проверяет конверт.
    /// Не делает COMMIT и ROLLBACK: HTTP коммитит сам, worker — только вместе с <c>finish_job</c>.
    /// </summary>
    /// <param name="connection">Открытое соединение.</param>
    /// <param name="transaction">Открытая транзакция вызывающего.</param>
    /// <param name="manifest">Манифест из каталога или из claim.</param>
    /// <param name="context">Доверенный контекст.</param>
    /// <param name="payloadJson">JSON-объект payload.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Успех или ошибка конверта без изменения транзакции.</returns>
    public async Task<ActionInvokeParseResult> InvokeOnConnectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActionManifest manifest,
        TrustedContext context,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

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
        return ActionInvokeEnvelope.Parse(json, manifest, _validator);
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
            ActionInvokeParseResult parsed = await InvokeOnConnectionAsync(
                    connection,
                    transaction,
                    manifest,
                    context,
                    payloadJson,
                    cancellationToken)
                .ConfigureAwait(false);
            return await CompleteInvokeAsync(manifest, context, parsed, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!httpCancellation.IsCancellationRequested)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            await PersistErrorDispatchAsync(manifest, context, cancellationToken).ConfigureAwait(false);
            return Fail(ErrorCodes.ActionTimeout, "action timed out", context.CorrelationId, manifest.Version);
        }
        catch (PostgresException exception) when (exception.SqlState == "57014")
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            await PersistErrorDispatchAsync(manifest, context, cancellationToken).ConfigureAwait(false);
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
                await PersistErrorDispatchAsync(manifest, context, cancellationToken).ConfigureAwait(false);
                return Fail(ErrorCodes.InternalError, "action failed", context.CorrelationId, manifest.Version);
            }

            return Fail(ErrorCodes.DependencyUnavailable, "PostgreSQL is unavailable", context.CorrelationId, manifest.Version);
        }
        catch (InvalidOperationException)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            await PersistErrorDispatchAsync(manifest, context, cancellationToken).ConfigureAwait(false);
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
        TrustedContext context,
        ActionInvokeParseResult parsed,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (parsed is ActionInvokeParseResult.Failure failure)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await PersistErrorDispatchAsync(manifest, context, cancellationToken).ConfigureAwait(false);
            return Fail(
                failure.Code,
                SanitizeMessage(failure.Code, failure.Message),
                context.CorrelationId,
                manifest.Version);
        }

        ActionInvokeParseResult.Success success = (ActionInvokeParseResult.Success)parsed;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ActionHttpSuccess(
            new HttpSuccessEnvelope(
                "ok",
                success.Outcome,
                success.Result,
                new HttpResponseMeta(context.CorrelationId, manifest.Version)));
    }

    private async Task PersistErrorDispatchAsync(
        ActionManifest manifest,
        TrustedContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await _connections
                .OpenRuntimeConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                "SELECT api.record_error_dispatch(@context::jsonb, @module, @action, @version)",
                connection);
            command.Parameters.AddWithValue(
                "context",
                JsonSerializer.Serialize(ToInvokeContext(context), ContextJsonOptions));
            command.Parameters.AddWithValue("module", manifest.Module);
            command.Parameters.AddWithValue("action", manifest.Action);
            command.Parameters.AddWithValue("version", manifest.Version);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static bool RequiresIdempotencyKey(string mode)
    {
        return !string.Equals(mode, "none", StringComparison.Ordinal)
            && !string.Equals(mode, "optional", StringComparison.Ordinal);
    }

    private static InvokeContextDto ToInvokeContext(TrustedContext context)
    {
        InvokeTransportDto? transport = context.Transport is null
            ? null
            : new InvokeTransportDto(context.Transport.SignatureVerified, context.Transport.SignatureVersion);
        return new InvokeContextDto(
            context.Principal,
            context.Consumer,
            context.Scopes,
            context.CorrelationId,
            context.RequestId,
            context.Deadline,
            context.ExecutionId,
            context.ProcessId,
            context.JobId,
            context.AttemptId,
            transport);
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

    private sealed record InvokeTransportDto(bool SignatureVerified, int SignatureVersion);

    private sealed record InvokeContextDto(
        string Principal,
        string Consumer,
        IReadOnlyList<string> Scopes,
        Guid CorrelationId,
        string? RequestId,
        DateTimeOffset Deadline,
        string? ExecutionId,
        string? ProcessId,
        string? JobId,
        string? AttemptId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        InvokeTransportDto? Transport);
}
