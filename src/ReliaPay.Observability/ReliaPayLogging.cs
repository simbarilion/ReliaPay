namespace ReliaPay.Observability;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

/// <summary>
/// Общие log-scope и JSON-консоль для процессов ReliaPay.
/// </summary>
public static class ReliaPayLogging
{
    /// <summary>
    /// Подключает однострочный JSON formatter в stdout. Failpoint ack пишется отдельно в Console.Out
    /// и этим formatter не оборачивается.
    /// </summary>
    /// <param name="builder">Конфигуратор Microsoft.Extensions.Logging.</param>
    public static void ConfigureJsonConsole(ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ClearProviders();
        builder.AddJsonConsole(static options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            options.JsonWriterOptions = new JsonWriterOptions
            {
                Indented = false,
            };
        });
        builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
        builder.AddFilter("ReliaPay", LogLevel.Information);
    }

    /// <summary>
    /// Собирает scope со строковыми значениями, чтобы консоль писала поля, а не имена типов.
    /// Без JWT и payload.
    /// </summary>
    /// <param name="trace">Безопасный след запроса.</param>
    /// <returns>Словарь для <c>ILogger.BeginScope</c>.</returns>
    public static IReadOnlyDictionary<string, object> ToScope(RequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Dictionary<string, object> scope = new(StringComparer.Ordinal)
        {
            ["correlationId"] = trace.CorrelationId.ToString("D"),
            ["httpRoute"] = trace.Route,
        };
        if (trace.RequestId is not null)
        {
            scope["requestId"] = trace.RequestId;
        }

        if (trace.Module is not null)
        {
            scope["module"] = trace.Module;
        }

        if (trace.Action is not null)
        {
            scope["action"] = trace.Action;
        }

        return scope;
    }

    /// <summary>
    /// Scope worker-job: только известные workflow id, без payload и секретов.
    /// </summary>
    /// <param name="correlationId">UUID попытки, если известен.</param>
    /// <param name="jobId">Идентификатор job.</param>
    /// <param name="processId">Идентификатор process.</param>
    /// <param name="executionId">Идентификатор execution.</param>
    /// <param name="attemptId">Идентификатор attempt.</param>
    /// <param name="leaseVersion">Версия lease.</param>
    /// <param name="module">Модуль action.</param>
    /// <param name="action">Имя action.</param>
    /// <returns>Словарь для <c>ILogger.BeginScope</c>.</returns>
    public static IReadOnlyDictionary<string, object> ToJobScope(
        Guid correlationId,
        Guid jobId,
        Guid processId,
        Guid executionId,
        Guid attemptId,
        long leaseVersion,
        string module,
        string action)
    {
        Dictionary<string, object> scope = new(StringComparer.Ordinal)
        {
            ["correlationId"] = correlationId.ToString("D"),
            ["jobId"] = jobId.ToString("D"),
            ["processId"] = processId.ToString("D"),
            ["executionId"] = executionId.ToString("D"),
            ["attemptId"] = attemptId.ToString("D"),
            ["leaseVersion"] = leaseVersion,
            ["requestId"] = executionId.ToString("D"),
        };
        if (!string.IsNullOrWhiteSpace(module))
        {
            scope["module"] = module;
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            scope["action"] = action;
        }

        return scope;
    }
}
