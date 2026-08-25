namespace ReliaPay.Observability;

/// <summary>
/// Собирает <see cref="RequestTrace"/> из пути и заголовков без чтения тела запроса.
/// </summary>
public static class RequestTraceFactory
{
    /// <summary>
    /// Внутренний заголовок корреляции между gateway и api. Клиентский одноимённый заголовок шлюз игнорирует.
    /// </summary>
    public const string CorrelationHeaderName = "X-Correlation-Id";

    /// <summary>
    /// Строит след запроса. При <paramref name="mintCorrelationId"/> всегда новый UUID.
    /// </summary>
    /// <param name="path">HTTP-путь, возможно с query.</param>
    /// <param name="incomingCorrelationId">Значение входящего заголовка корреляции.</param>
    /// <param name="idempotencyKey">Заголовок Idempotency-Key.</param>
    /// <param name="mintCorrelationId">Истина на публичном крае: не доверять входящему UUID.</param>
    /// <returns>Типизированный след без payload и JWT.</returns>
    public static RequestTrace Create(
        string path,
        string? incomingCorrelationId,
        string? idempotencyKey,
        bool mintCorrelationId)
    {
        Guid correlationId = mintCorrelationId
            ? Guid.NewGuid()
            : TryParseCorrelationId(incomingCorrelationId) ?? Guid.NewGuid();
        string route = NormalizeRoute(path);
        TryParseModuleAction(route, out string? module, out string? action);
        string? requestId = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        return new RequestTrace(correlationId, requestId, route, module, action);
    }

    /// <summary>
    /// Принимает только непустой UUID. Нулевой UUID считается невалидным.
    /// </summary>
    private static Guid? TryParseCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Guid.TryParse(value.Trim(), out Guid parsed)
            || parsed == Guid.Empty)
        {
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// Отбрасывает query и приводит путь к виду, начинающемуся с <c>/</c>.
    /// </summary>
    private static string NormalizeRoute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        int query = path.IndexOf('?', StringComparison.Ordinal);
        string withoutQuery = query >= 0 ? path[..query] : path;
        return withoutQuery.StartsWith('/') ? withoutQuery : "/" + withoutQuery;
    }

    /// <summary>
    /// Извлекает module/action из <c>/api/{module}/{action}</c> и OpenAPI per-version пути.
    /// </summary>
    private static void TryParseModuleAction(string route, out string? module, out string? action)
    {
        module = null;
        action = null;
        string[] parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            module = parts[1];
            action = parts[2];
            return;
        }

        if (parts.Length >= 4
            && parts[0].Equals("openapi", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("actions", StringComparison.OrdinalIgnoreCase))
        {
            module = parts[2];
            action = parts[3];
        }
    }
}
