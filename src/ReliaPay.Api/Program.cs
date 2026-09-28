using System.Text.Json;
using ReliaPay.Api.Health;
using ReliaPay.Api.Http;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Data;
using ReliaPay.Execution;
using ReliaPay.Json;
using ReliaPay.Observability;
using ReliaPay.OpenApi;
using ReliaPay.Security;

namespace ReliaPay.Api;

/// <summary>
/// Точка входа внутреннего action runtime. Host-порт наружу не публикуется; доступен только через gateway.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Запускает HTTP-процесс api: health, OpenAPI и generic POST /api/{module}/{action}.
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
    /// Регистрирует JWT, catalog, executor и JSON camelCase.
    /// </summary>
    /// <param name="builder">Построитель веб-приложения api.</param>
    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(static options =>
        {
            options.Limits.MaxRequestBodySize = CourseHttpLimits.MaxRequestBodyBytes;
        });
        builder.AddReliaPayTracing("ReliaPay.Api", mintCorrelationId: false);
        builder.Services.ConfigureHttpJsonOptions(static options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });
        builder.Services.AddSingleton(JwtOptions.FromEnvironment());
        builder.Services.AddSingleton(ProviderHmacOptions.FromEnvironment());
        builder.Services.AddSingleton<JwtAuthenticator>();
        builder.Services.AddSingleton(_ => new PostgresConnectionFactory(PostgresEndpoints.FromRuntimeEnvironment()));
        builder.Services.AddSingleton<ActionCatalogReader>();
        builder.Services.AddSingleton<JsonDocumentValidator>();
        builder.Services.AddSingleton(_ => CourseFailpoint.FromEnvironment());
        builder.Services.AddSingleton<ActionExecutor>();
        builder.Services.AddSingleton<OpenApiDocumentFactory>();
    }

    /// <summary>
    /// Каркас маршрутов внутреннего runtime.
    /// </summary>
    /// <param name="app">Собранное веб-приложение.</param>
    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseReliaPayTracing();
        app.Use(HideUnhandledExceptionsAsync);
        app.MapGet("/health/live", () => Results.Json(new ApiHealthPayload("live")));
        app.MapGet("/health/ready", CheckReadyAsync);
        app.MapGet("/openapi/default.json", GetDefaultOpenApiAsync);
        app.MapGet("/openapi/actions/{module}/{action}/{fileName}", GetActionOpenApiAsync);
        app.MapPost("/api/{module}/{action}", ExecuteActionAsync);
        app.Map("/api/{*rest}", static () => ApiJson.Error(
            StatusCodes.Status400BadRequest,
            HttpErrorEnvelope.Create(
                ErrorCodes.RequestInvalid,
                "route is not allowed",
                retryable: false,
                correlationId: null,
                actionVersion: null)));
    }

    /// <summary>
    /// Готовность: PostgreSQL доступен и проекция contract_info уже существует.
    /// </summary>
    private static async Task<IResult> CheckReadyAsync(ActionCatalogReader catalog, CancellationToken cancellationToken)
    {
        bool ready = await catalog.IsReadyAsync(cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            return Results.Json(new ApiHealthPayload("ready"));
        }

        return Results.Json(
            new ApiHealthPayload("not_ready", "dependency.unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// OpenAPI только включённых default-маршрутов, без пересборки после activate/disable.
    /// </summary>
    private static async Task<IResult> GetDefaultOpenApiAsync(
        ActionCatalogReader catalog,
        OpenApiDocumentFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ActionManifest> defaults = await catalog.ListDefaultsAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(factory.CreateDefault(defaults), ApiJson.ResponseOptions);
        }
        catch (PostgresUnavailableException)
        {
            return DependencyUnavailable();
        }
    }

    /// <summary>
    /// OpenAPI одной точной версии опубликованного action.
    /// </summary>
    private static async Task<IResult> GetActionOpenApiAsync(
        string module,
        string action,
        string fileName,
        ActionCatalogReader catalog,
        OpenApiDocumentFactory factory,
        CancellationToken cancellationToken)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(fileName[..^5], out int version)
            || version < 1)
        {
            return ApiJson.Error(
                StatusCodes.Status400BadRequest,
                HttpErrorEnvelope.Create(
                    ErrorCodes.RequestInvalid,
                    "OpenAPI version path is invalid",
                    retryable: false,
                    correlationId: null,
                    actionVersion: null));
        }

        try
        {
            ActionManifest? manifest = await catalog
                .GetPublishedAsync(module, action, version, cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null)
            {
                return ApiJson.Error(
                    StatusCodes.Status404NotFound,
                    HttpErrorEnvelope.Create(
                        ErrorCodes.ActionNotFound,
                        "action is not available",
                        retryable: false,
                        correlationId: null,
                        actionVersion: null));
            }

            return Results.Json(factory.CreateAction(manifest), ApiJson.ResponseOptions);
        }
        catch (PostgresUnavailableException)
        {
            return DependencyUnavailable();
        }
    }

    /// <summary>
    /// Generic executor: JWT, schema, одна транзакция, api.invoke.
    /// </summary>
    private static async Task<IResult> ExecuteActionAsync(
        HttpContext httpContext,
        JwtAuthenticator authenticator,
        ActionExecutor executor,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(httpContext.Request.Method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase))
        {
            return InvalidRoute();
        }

        string path = httpContext.Request.Path.Value ?? string.Empty;
        string? versionHeader = httpContext.Request.Headers["X-Action-Version"].ToString();
        if (string.IsNullOrEmpty(versionHeader))
        {
            versionHeader = null;
        }

        if (!ActionRouteParser.TryParse(path, versionHeader, out ActionRoute? route) || route is null)
        {
            return InvalidRoute();
        }

        JwtPrincipal principal = await authenticator
            .AuthenticateAsync(httpContext.Request.Headers.Authorization.ToString())
            .ConfigureAwait(false);
        Guid correlationId = ReliaPayTraceHttp.Get(httpContext)?.CorrelationId ?? Guid.NewGuid();
        if (!principal.IsAuthenticated)
        {
            return ApiJson.Error(
                StatusCodes.Status401Unauthorized,
                HttpErrorEnvelope.Create(
                    ErrorCodes.AuthInvalid,
                    "authentication failed",
                    retryable: false,
                    correlationId: null,
                    actionVersion: null));
        }

        string? idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            idempotencyKey = null;
        }

        string? providerSignature = httpContext.Request.Headers[ProviderHmac.HeaderName].ToString();
        if (string.IsNullOrEmpty(providerSignature))
        {
            providerSignature = null;
        }

        byte[]? payloadBytes = await BoundedJsonBody
            .ReadBytesAsync(httpContext.Request.Body, httpContext.Request.ContentLength, cancellationToken)
            .ConfigureAwait(false);
        if (payloadBytes is null)
        {
            return ApiJson.Error(
                StatusCodes.Status400BadRequest,
                HttpErrorEnvelope.Create(
                    ErrorCodes.RequestInvalid,
                    "request body is too large",
                    retryable: false,
                    correlationId: correlationId,
                    actionVersion: null));
        }

        ActionHttpResult result = await executor
            .ExecuteAsync(
                route,
                principal,
                idempotencyKey,
                payloadBytes,
                providerSignature,
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            ActionHttpSuccess success => ApiJson.Ok(success.Envelope),
            ActionHttpFailure failure => ApiJson.Error(failure.StatusCode, failure.Envelope),
            _ => ApiJson.Error(
                StatusCodes.Status500InternalServerError,
                HttpErrorEnvelope.Create(
                    ErrorCodes.InternalError,
                    "action failed",
                    retryable: false,
                    correlationId: correlationId,
                    actionVersion: null)),
        };
    }

    /// <summary>
    /// Скрывает необработанные исключения единым 500 без SQL и stack.
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
                        "action failed",
                        retryable: false,
                        correlationId: ReliaPayTraceHttp.Get(context)?.CorrelationId,
                        actionVersion: null),
                    ApiJson.ResponseOptions,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
    }



    private static IResult InvalidRoute()
    {
        return ApiJson.Error(
            StatusCodes.Status400BadRequest,
            HttpErrorEnvelope.Create(
                ErrorCodes.RequestInvalid,
                "route is not allowed",
                retryable: false,
                correlationId: null,
                actionVersion: null));
    }

    private static IResult DependencyUnavailable()
    {
        return ApiJson.Error(
            StatusCodes.Status503ServiceUnavailable,
            HttpErrorEnvelope.Create(
                ErrorCodes.DependencyUnavailable,
                "PostgreSQL is unavailable",
                retryable: true,
                correlationId: null,
                actionVersion: null));
    }
}
