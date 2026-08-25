using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReliaPay.Contracts;

namespace ReliaPay.Observability;

/// <summary>
/// Регистрация OpenTelemetry и HTTP-middleware трассировки для gateway и api.
/// </summary>
public static class ReliaPayTracingExtensions
{
    /// <summary>
    /// Подключает консоль со scopes, ActivitySource ReliaPay и instrumentation без JWT и payload.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения.</param>
    /// <param name="serviceName">Имя ресурса OpenTelemetry.</param>
    /// <param name="mintCorrelationId">Истина для gateway: не принимать клиентский UUID.</param>
    /// <returns>Тот же builder.</returns>
    public static WebApplicationBuilder AddReliaPayTracing(
        this WebApplicationBuilder builder,
        string serviceName,
        bool mintCorrelationId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceName);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.IncludeScopes = true;
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss.fff ";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
        builder.Logging.AddFilter("ReliaPay", LogLevel.Information);
        builder.Services.AddSingleton(new ReliaPayTraceOptions
        {
            ServiceName = serviceName,
            MintCorrelationId = mintCorrelationId,
        });
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: CourseContracts.Version))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ReliaPayInstrumentation.ActivitySourceName);
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = static context => !ReliaPayTraceMiddleware.IsHealthPath(context.Request.Path);
                    options.EnrichWithHttpRequest = static (activity, request) =>
                    {
                        activity.SetTag("http.route", request.Path.Value);
                    };
                });
                tracing.AddHttpClientInstrumentation(options =>
                {
                    options.EnrichWithHttpRequestMessage = static (activity, message) =>
                    {
                        activity.SetTag("http.route", message.RequestUri?.AbsolutePath);
                        RedactSensitiveActivityTags(activity);
                    };
                });
            });
        return builder;
    }

    /// <summary>
    /// Ставит middleware трассировки первым, чтобы scope покрывал ошибки и прокси.
    /// </summary>
    /// <param name="app">Конвейер ASP.NET Core.</param>
    /// <returns>Тот же конвейер.</returns>
    public static IApplicationBuilder UseReliaPayTracing(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ReliaPayTraceMiddleware>();
    }

    /// <summary>
    /// Стирает случайно попавшие секретные теги, если инструментация их добавила.
    /// </summary>
    private static void RedactSensitiveActivityTags(Activity activity)
    {
        foreach (KeyValuePair<string, object?> tag in activity.TagObjects.ToArray())
        {
            if (tag.Key is not null && LogRedaction.IsSensitiveTag(tag.Key))
            {
                activity.SetTag(tag.Key, "[redacted]");
            }
        }
    }
}
