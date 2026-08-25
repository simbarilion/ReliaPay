using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Gateway.Health;
using ReliaPay.Gateway.Routing;

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
    /// Запускает HTTP-процесс шлюза с whitelist, liveness и проверкой готовности внутреннего api.
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
    /// Регистрирует типизированные настройки, HTTP-клиент пробы api и JSON с camelCase.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения шлюза.</param>
    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(CreateGatewayOptions(builder.Configuration));
        builder.Services.AddSingleton<GatewayRouteWhitelist>();
        builder.Services.AddHttpClient<ApiReadinessProbe>(static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(2);
        });
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });
    }

    /// <summary>
    /// Каркас конвейера: отсечение не-whitelist путей, health live/ready. Проксирование actions — следующий этап.
    /// </summary>
    /// <param name="app">Собранное веб-приложение.</param>
    private static void ConfigurePipeline(WebApplication app)
    {
        app.Use(RejectUnknownRoutesAsync);
        app.MapGet("/health/live", () => Results.Json(new GatewayHealthPayload("live")));
        app.MapGet("/health/ready", CheckReadyAsync);
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
    /// Возвращает 200, если внутренний api отвечает на liveness, иначе 503 без утечки внутренних адресов.
    /// </summary>
    /// <param name="probe">Проба зависимости api.</param>
    /// <param name="cancellationToken">Токен отмены запроса.</param>
    /// <returns>JSON health-ответ.</returns>
    private static async Task<IResult> CheckReadyAsync(ApiReadinessProbe probe, CancellationToken cancellationToken)
    {
        bool ready = await probe.IsApiLiveAsync(cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            return Results.Json(new GatewayHealthPayload("ready"));
        }

        return Results.Json(new GatewayHealthPayload("unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
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
}
