using Microsoft.AspNetCore.Http;

namespace ReliaPay.Observability;

/// <summary>
/// Кладёт <see cref="RequestTrace"/> в <see cref="HttpContext.Items"/>, чтобы executor и прокси читали один UUID.
/// </summary>
public static class ReliaPayTraceHttp
{
    private static readonly object ItemKey = typeof(RequestTrace);

    /// <summary>
    /// Сохраняет след текущего запроса.
    /// </summary>
    /// <param name="context">HTTP-контекст.</param>
    /// <param name="trace">Собранный след.</param>
    public static void Set(HttpContext context, RequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trace);
        context.Items[ItemKey] = trace;
    }

    /// <summary>
    /// Читает след, если middleware уже отработал.
    /// </summary>
    /// <param name="context">HTTP-контекст.</param>
    /// <returns>След либо null.</returns>
    public static RequestTrace? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out object? stored) ? stored as RequestTrace : null;
    }
}
