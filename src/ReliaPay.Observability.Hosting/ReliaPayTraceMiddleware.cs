using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ReliaPay.Observability;

/// <summary>
/// Middleware трассировки: correlationId, logger scope, Activity tags. Не читает и не пишет тело запроса.
/// </summary>
public sealed class ReliaPayTraceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ReliaPayTraceMiddleware> _logger;
    private readonly ReliaPayTraceOptions _options;

    /// <summary>
    /// Создаёт middleware поверх следующего звена конвейера.
    /// </summary>
    /// <param name="next">Следующий делегат.</param>
    /// <param name="logger">Журнал без секретов: только метод, маршрут и статус.</param>
    /// <param name="options">Кто mintит correlationId.</param>
    public ReliaPayTraceMiddleware(
        RequestDelegate next,
        ILogger<ReliaPayTraceMiddleware> logger,
        ReliaPayTraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Health-проверки не трассируем, чтобы пробы Compose не забивали журнал.
    /// </summary>
    /// <param name="path">Путь запроса.</param>
    /// <returns>Истина для <c>/health/*</c>.</returns>
    public static bool IsHealthPath(PathString path)
    {
        return path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Собирает след, открывает Activity и scope, затем вызывает следующий middleware.
    /// </summary>
    /// <param name="context">Текущий HTTP-контекст.</param>
    /// <returns>Задача завершения запроса.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsHealthPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? incoming = context.Request.Headers[RequestTraceFactory.CorrelationHeaderName];
        string? idempotencyKey = context.Request.Headers["Idempotency-Key"];
        RequestTrace trace = RequestTraceFactory.Create(
            context.Request.Path.Value ?? "/",
            incoming,
            string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey.ToString(),
            _options.MintCorrelationId);
        ReliaPayTraceHttp.Set(context, trace);
        ReliaPayActivity.ApplyTags(Activity.Current, trace);
        using Activity? activity = ReliaPayInstrumentation.ActivitySource.StartActivity("http.request");
        ReliaPayActivity.ApplyTags(activity, trace);
        using IDisposable? scope = _logger.BeginScope(
            "correlationId:{correlationId} requestId:{requestId} module:{module} action:{action}",
            trace.CorrelationId,
            trace.RequestId ?? "-",
            trace.Module ?? "-",
            trace.Action ?? "-");
        _logger.LogInformation(
            "HTTP {httpMethod} {httpRoute} started correlationId={correlationId}",
            context.Request.Method,
            trace.Route,
            trace.CorrelationId);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            _logger.LogInformation(
                "HTTP {httpMethod} {httpRoute} -> {statusCode} correlationId={correlationId}",
                context.Request.Method,
                trace.Route,
                context.Response.StatusCode,
                trace.CorrelationId);
        }
    }
}
