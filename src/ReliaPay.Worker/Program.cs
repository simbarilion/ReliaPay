using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReliaPay.Data;
using ReliaPay.Execution;
using ReliaPay.Json;
using ReliaPay.Observability;

namespace ReliaPay.Worker;

/// <summary>
/// Точка входа generic worker. Два Compose-сервиса запускают один и тот же образ.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Ждёт уведомления о claim до SIGTERM.
    /// </summary>
    /// <param name="args">Аргументы командной строки; worker их не использует.</param>
    /// <returns>Код выхода.</returns>
    public static async Task<int> Main(string[] args)
    {
        _ = args;
        using CancellationTokenSource shutdown = new();
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            _ => shutdown.Cancel());
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            _ => shutdown.Cancel());
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        using ILoggerFactory loggerFactory = LoggerFactory.Create(ReliaPayLogging.ConfigureJsonConsole);
        ILogger<WorkflowWorker> logger = loggerFactory.CreateLogger<WorkflowWorker>();

        try
        {
            WorkerSettings settings = WorkerSettings.FromEnvironment();
            PostgresConnectionFactory connections = new(PostgresEndpoints.FromWorkerEnvironment());
            JsonDocumentValidator validator = new();
            ActionExecutor executor = new(connections, validator);
            WorkflowWorkerStore store = new(connections);
            WorkerFailpoint failpoint = new(settings.Failpoint, Console.Out);
            WorkflowWorker worker = new(settings, store, executor, validator, failpoint, logger);
            await using ObservabilityHost obs = new(connections);
            obs.Start();
            await worker.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError("Worker failed to start: {errorCode}", "startup.invalid");
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
