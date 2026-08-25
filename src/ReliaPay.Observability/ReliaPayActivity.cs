using System.Diagnostics;

namespace ReliaPay.Observability;

/// <summary>
/// Проставляет согласованные Activity tags без секретов и без тела запроса.
/// </summary>
public static class ReliaPayActivity
{
    /// <summary>
    /// Добавляет <c>http.route</c>, <c>module</c>, <c>action</c>, <c>correlation.id</c> и при наличии <c>request.id</c>.
    /// </summary>
    /// <param name="activity">Текущий span либо null, если слушателей нет.</param>
    /// <param name="trace">Серверный след запроса.</param>
    public static void ApplyTags(Activity? activity, RequestTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("http.route", trace.Route);
        activity.SetTag("correlation.id", trace.CorrelationId.ToString("D"));
        if (trace.Module is not null)
        {
            activity.SetTag("module", trace.Module);
        }

        if (trace.Action is not null)
        {
            activity.SetTag("action", trace.Action);
        }

        if (trace.RequestId is not null)
        {
            activity.SetTag("request.id", trace.RequestId);
        }
    }
}
