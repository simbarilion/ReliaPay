namespace ReliaPay.Observability;

/// <summary>
/// Поля лог-scope, по которым склеиваются строки gateway и api.
/// </summary>
public static class ReliaPayLogging
{
    /// <summary>
    /// Словарь scope со строковыми значениями, чтобы консоль печатала пары, а не имя типа.
    /// Без JWT и payload.
    /// </summary>
    /// <param name="trace">Серверный след запроса.</param>
    /// <returns>Пары для <c>ILogger.BeginScope</c>.</returns>
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
}
