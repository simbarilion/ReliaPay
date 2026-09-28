using Microsoft.Extensions.Logging;
using Npgsql;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Data;
using ReliaPay.Execution;
using ReliaPay.Json;
using ReliaPay.Observability;
using ReliaPay.Security;

namespace ReliaPay.Worker;

/// <summary>
/// Цикл generic worker: claim (autocommit), затем invoke+finish в одной транзакции.
/// </summary>
public sealed class WorkflowWorker
{
    private readonly WorkerSettings _settings;
    private readonly WorkflowWorkerStore _store;
    private readonly ActionExecutor _executor;
    private readonly JsonDocumentValidator _validator;
    private readonly WorkerFailpoint _failpoint;
    private readonly ILogger<WorkflowWorker> _logger;

    /// <summary>
    /// Создаёт цикл поверх store, executor и failpoint.
    /// </summary>
    /// <param name="settings">Идентичность экземпляра и интервал poll.</param>
    /// <param name="store">Вызовы claim/finish/fail.</param>
    /// <param name="executor">Invoke без COMMIT.</param>
    /// <param name="validator">Проверка mapped payload по request schema.</param>
    /// <param name="failpoint">Учебные точки зависания.</param>
    public WorkflowWorker(
        WorkerSettings settings,
        WorkflowWorkerStore store,
        ActionExecutor executor,
        JsonDocumentValidator validator,
        WorkerFailpoint failpoint,
        ILogger<WorkflowWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(failpoint);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _store = store;
        _executor = executor;
        _validator = validator;
        _failpoint = failpoint;
        _logger = logger;
    }

    /// <summary>
    /// Крутит claim до отмены контейнера.
    /// </summary>
    /// <param name="cancellationToken">SIGTERM/SIGINT контейнера.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool claimed = await TryClaimAndRunAsync(cancellationToken).ConfigureAwait(false);
                if (!claimed)
                {
                    await Task.Delay(_settings.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (PostgresUnavailableException)
            {
                await DelayQuietlyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await DelayQuietlyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryClaimAndRunAsync(CancellationToken cancellationToken)
    {
        string json = await _store
            .ClaimJobsAsync(_settings.InstanceId, _settings.LeaseMs, cancellationToken)
            .ConfigureAwait(false);
        ClaimParseResult parsed = ClaimedJobParser.Parse(json);
        if (parsed.ErrorCode is not null || parsed.Jobs.Count == 0)
        {
            return false;
        }

        foreach (ClaimedJob job in parsed.Jobs)
        {
            await ProcessJobAsync(job, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task ProcessJobAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        Guid correlationId = Guid.NewGuid();
        using IDisposable? scope = _logger.BeginScope(
            ReliaPayLogging.ToJobScope(
                correlationId,
                job.JobId,
                job.ProcessId,
                job.ExecutionId,
                job.AttemptId,
                job.LeaseVersion,
                job.Module,
                job.Action));
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation("Worker job claimed");

        await _failpoint
            .HitAsync(WorkerFailpoint.AfterJobClaim, _settings.InstanceId, cancellationToken)
            .ConfigureAwait(false);

        if (!WorkflowInputMapper.TryMap(
                job.ProcessData,
                job.InputMapping,
                job.InputConstants,
                out string payloadJson,
                out string? mappingError))
        {
            await FailIgnoringStaleAsync(
                    job,
                    mappingError ?? ErrorCodes.WorkflowMappingMissing,
                    retryable: false,
                    cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", mappingError ?? ErrorCodes.WorkflowMappingMissing, started);
            return;
        }

        SchemaValidationResult payloadCheck = _validator.Validate(payloadJson, job.RequestSchema.GetRawText());
        if (!payloadCheck.IsValid)
        {
            await FailIgnoringStaleAsync(job, ErrorCodes.PayloadInvalid, retryable: false, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.PayloadInvalid, started);
            return;
        }

        ActionManifest manifest = job.ToManifest();
        TrustedContext context = job.ToTrustedContext(correlationId, DateTimeOffset.UtcNow);

        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await _store.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(job.TimeoutMs);
            ActionInvokeParseResult invoked = await _executor
                .InvokeOnConnectionAsync(connection, transaction, manifest, context, payloadJson, timeout.Token)
                .ConfigureAwait(false);
            if (invoked is ActionInvokeParseResult.Failure failure)
            {
                await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
                await FailIgnoringStaleAsync(
                        job,
                        failure.Code,
                        failure.Retryable,
                        cancellationToken)
                    .ConfigureAwait(false);
                LogJobFinished("failed", failure.Code, started);
                return;
            }

            ActionInvokeParseResult.Success success = (ActionInvokeParseResult.Success)invoked;
            await _failpoint
                .HitAsync(WorkerFailpoint.AfterActionBeforeFinish, _settings.InstanceId, cancellationToken)
                .ConfigureAwait(false);

            string finished = await _store
                .FinishJobAsync(
                    connection,
                    transaction,
                    job,
                    success.Outcome,
                    success.Result.GetRawText(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (WorkflowWorkerStore.IsLeaseStale(finished))
            {
                await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
                LogJobFinished("lease_stale", "workflow.lease_stale", started);
                return;
            }

            if (!WorkflowWorkerStore.IsOk(finished))
            {
                await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
                string code = WorkflowWorkerStore.ErrorCode(finished) ?? ErrorCodes.InternalError;
                await FailIgnoringStaleAsync(
                        job,
                        code,
                        JobFailurePolicy.IsRetryable(code),
                        cancellationToken)
                    .ConfigureAwait(false);
                LogJobFinished("failed", code, started);
                return;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogJobFinished("ok", success.Outcome, started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            await FailIgnoringStaleAsync(job, ErrorCodes.ActionTimeout, retryable: true, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.ActionTimeout, started);
        }
        catch (PostgresException exception) when (exception.SqlState == "57014")
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            await FailIgnoringStaleAsync(job, ErrorCodes.ActionTimeout, retryable: true, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.ActionTimeout, started);
        }
        catch (PostgresUnavailableException)
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            await FailIgnoringStaleAsync(job, ErrorCodes.DependencyUnavailable, retryable: true, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.DependencyUnavailable, started);
        }
        catch (NpgsqlException exception) when (exception is not PostgresException)
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            await FailIgnoringStaleAsync(job, ErrorCodes.DependencyUnavailable, retryable: true, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.DependencyUnavailable, started);
        }
        catch (NpgsqlException)
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            await FailIgnoringStaleAsync(job, ErrorCodes.InternalError, retryable: true, cancellationToken)
                .ConfigureAwait(false);
            LogJobFinished("failed", ErrorCodes.InternalError, started);
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

    private async Task FailIgnoringStaleAsync(
        ClaimedJob job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        try
        {
            string json = await _store
                .FailJobAsync(job, errorCode, retryable, cancellationToken)
                .ConfigureAwait(false);
            _ = WorkflowWorkerStore.IsLeaseStale(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }
    }


    private void LogJobFinished(string status, string outcomeOrCode, long startedTimestamp)
    {
        double durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "Worker job finished status={status} outcome={outcome} durationMs={durationMs}",
            status,
            outcomeOrCode,
            Math.Round(durationMs, 3));
    }

    private async Task DelayQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_settings.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RollbackQuietlyAsync(NpgsqlTransaction? transaction)
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
}
