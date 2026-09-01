using System.Runtime.InteropServices;
using ReliaPay.Data;
using ReliaPay.Execution;
using ReliaPay.Json;

namespace ReliaPay.Worker;

/// <summary>
/// Точка входа generic worker. Два Compose-сервиса запускают один и тот же образ.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Читает окружение и крутит claim до SIGTERM.
    /// </summary>
    /// <param name="args">Аргументы процесса; worker их не использует.</param>
    /// <returns>Код процесса.</returns>
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

        try
        {
            WorkerSettings settings = WorkerSettings.FromEnvironment();
            PostgresConnectionFactory connections = new(PostgresEndpoints.FromWorkerEnvironment());
            JsonDocumentValidator validator = new();
            ActionExecutor executor = new(connections, validator);
            WorkflowWorkerStore store = new(connections);
            WorkerFailpoint failpoint = new(settings.Failpoint, Console.Out);
            WorkflowWorker worker = new(settings, store, executor, validator, failpoint);
            await worker.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
