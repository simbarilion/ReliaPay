using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Gateway.Health;
using ReliaPay.Gateway.Proxy;
using ReliaPay.Gateway.Routing;
using ReliaPay.Observability;

namespace ReliaPay.Gateway;

/// <summary>
/// Точка входа публичного API Gateway. Единственный процесс, публикующий host-порт 8080.
/// </summary>
public sealed class Program
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Запускает HTTP-процесс шлюза: whitelist, health и проксирование на внутренний api.
    /// </summary>
    /// <param name="args">Аргументы хоста ASP.NET Core.</param>
    /// <returns>Код завершения процесса.</returns>
    public static async Task<int> Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder);
        WebApplication app = builder.Build();
        ConfigurePipeline(app);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Регистрирует настройки, пробу ready api, HTTP-прокси и JSON camelCase.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения шлюза.</param>
    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.AddReliaPayTracing("ReliaPay.Gateway", mintCorrelationId: true);
        builder.Services.AddSingleton(CreateGatewayOptions(builder.Configuration));
        builder.Services.AddSingleton<GatewayRouteWhitelist>();
        builder.Services.AddHttpClient<ApiReadinessProbe>(static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(2);
        });
        builder.Services.AddHttpClient<GatewayProxy>(static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });
    }

    /// <summary>
    /// Конвейер: неизвестные пути отсекаются, health обслуживается локально, остальное уходит в api.
    /// </summary>
    /// <param name="app">Собранное веб-приложение.</param>
    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseReliaPayTracing();
        app.Use(HideUnhandledExceptionsAsync);
        app.Use(RejectUnknownRoutesAsync);
        app.MapGet("/health/live", () => Results.Json(new GatewayHealthPayload("live")));
        app.MapGet("/health/ready", CheckReadyAsync);
        app.Map("{*path}", ProxyAsync);
    }

    /// <summary>
    /// Читает базовый URL api из конфигурации. В Compose это DNS-имя сервиса, не localhost.
    /// </summary>
    /// <param name="configuration">Конфигурация процесса, включая переменные окружения.</param>
    /// <returns>Типизированные настройки шлюза.</returns>
    private static ReliaPay.Gateway.Configuration.GatewayOptions CreateGatewayOptions(IConfiguration configuration)
    {
        string apiBaseUrl = configuration["COURSE_API_BASE_URL"] ?? "http://api:8080";
        return new ReliaPay.Gateway.Configuration.GatewayOptions { ApiBaseUrl = new Uri(apiBaseUrl) };
    }

    /// <summary>
    /// Возвращает 200, если внутренний api готов, иначе 503 без утечки внутренних адресов.
    /// </summary>
    /// <param name="probe">Проба зависимости api.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>JSON health-ответ.</returns>
    private static async Task<IResult> CheckReadyAsync(ApiReadinessProbe probe, CancellationToken cancellationToken)
    {
        bool ready = await probe.IsApiReadyAsync(cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            return Results.Json(new GatewayHealthPayload("ready"));
        }

        return Results.Json(new GatewayHealthPayload("unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Проксирует whitelist-маршрут на внутренний api без разбора JWT и catalog.
    /// </summary>
    private static Task ProxyAsync(HttpContext context, GatewayProxy proxy, CancellationToken cancellationToken)
    {
        return proxy.ProxyAsync(context, cancellationToken);
    }

    /// <summary>
    /// Отклоняет пути вне whitelist единым error envelope, не проксируя их во внутренний контур.
    /// </summary>
    /// <param name="context">Текущий HTTP-контекст.</param>
    /// <param name="next">Следующий делегат конвейера.</param>
    /// <returns>Задача завершения фильтра.</returns>
    private static async Task RejectUnknownRoutesAsync(HttpContext context, RequestDelegate next)
    {
        GatewayRouteWhitelist whitelist = context.RequestServices.GetRequiredService<GatewayRouteWhitelist>();
        if (whitelist.IsAllowed(context.Request.Path.Value ?? string.Empty))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        HttpErrorEnvelope envelope = HttpErrorEnvelope.Create(
            ErrorCodes.RequestInvalid,
            "route is not allowed",
            retryable: false,
            correlationId: null,
            actionVersion: null);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response
            .WriteAsJsonAsync(envelope, ResponseJsonOptions, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Скрывает необработанные исключения единым 500 без внутренних адресов и stack.
    /// </summary>
    private static async Task HideUnhandledExceptionsAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response
                .WriteAsJsonAsync(
                    HttpErrorEnvelope.Create(
                        ErrorCodes.InternalError,
                        "request failed",
                        retryable: false,
                        correlationId: ReliaPayTraceHttp.Get(context)?.CorrelationId,
                        actionVersion: null),
                    ResponseJsonOptions,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
