using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.Cli;

/// <summary>
/// Команды runtime process: list, activate, start, get, signal, test-finish.
/// </summary>
internal static class FlowProcessCommand
{
    /// <summary>
    /// Список опубликованных версий карт.
    /// </summary>
    public static async Task<int> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            string json = await FlowMapCommand.CreateClient().ListAsync(cancellationToken).ConfigureAwait(false);
            return await WorkflowEnvelopeWriter.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Активирует версию карты.
    /// </summary>
    public static async Task<int> ActivateAsync(string flowName, int version, CancellationToken cancellationToken)
    {
        try
        {
            string json = await FlowMapCommand.CreateClient()
                .ActivateAsync(flowName, version, cancellationToken)
                .ConfigureAwait(false);
            return await WorkflowEnvelopeWriter.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Идемпотентный старт process.
    /// </summary>
    public static async Task<int> StartAsync(
        string flowName,
        string businessKey,
        string dataPath,
        CancellationToken cancellationToken)
    {
        string data;
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            data = "{}";
        }
        else if (!CliInput.Exists(dataPath))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "process data file does not exist")
                .ConfigureAwait(false);
        }
        else
        {
            data = await CliInput.ReadAsync(dataPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(data))
            {
                data = "{}";
            }
        }

        try
        {
            string json = await FlowMapCommand.CreateClient()
                .StartAsync(flowName, businessKey, data, cancellationToken)
                .ConfigureAwait(false);
            return await WorkflowEnvelopeWriter
                .WriteAsync(json, extra: new Dictionary<string, string> { ["operation"] = "started" })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Чтение process для <c>flow get</c>.
    /// </summary>
    public static async Task<int> GetAsync(string processId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(processId, out Guid id))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "process id is not a UUID")
                .ConfigureAwait(false);
        }

        try
        {
            string json = await FlowMapCommand.CreateClient().GetAsync(id, cancellationToken).ConfigureAwait(false);
            return await WorkflowEnvelopeWriter.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Идемпотентный сигнал.
    /// </summary>
    public static async Task<int> SignalAsync(
        string processId,
        string type,
        string messageId,
        string payloadPath,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(processId, out Guid id))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "process id is not a UUID")
                .ConfigureAwait(false);
        }

        if (!CliInput.Exists(payloadPath))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "signal payload file does not exist")
                .ConfigureAwait(false);
        }

        string payload = await CliInput.ReadAsync(payloadPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            payload = "{}";
        }

        try
        {
            string json = await FlowMapCommand.CreateClient()
                .SignalAsync(id, type, messageId, payload, cancellationToken)
                .ConfigureAwait(false);
            return await WorkflowEnvelopeWriter
                .WriteAsync(
                    json,
                    extra: new Dictionary<string, string>
                    {
                        ["processId"] = processId,
                        ["signalType"] = type,
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Тестовый finish с fencing. Только при <c>COURSE_TEST_PROFILE=1</c>.
    /// </summary>
    public static async Task<int> TestFinishAsync(
        string jobId,
        string owner,
        long leaseVersion,
        string outcome,
        string resultPath,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(jobId, out Guid id))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "job id is not a UUID")
                .ConfigureAwait(false);
        }

        if (!CliInput.Exists(resultPath))
        {
            return await CliFailure.WriteAsync(ErrorCodes.RequestInvalid, "result file does not exist")
                .ConfigureAwait(false);
        }

        string result = await CliInput.ReadAsync(resultPath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "{}";
        }

        try
        {
            string json = await FlowMapCommand.CreateClient()
                .FinishJobAsync(id, owner, leaseVersion, outcome, result, cancellationToken)
                .ConfigureAwait(false);
            return await WorkflowEnvelopeWriter.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PostgresUnavailableException or InvalidOperationException)
        {
            return await CliFailure.FromExceptionAsync(exception).ConfigureAwait(false);
        }
    }
}
