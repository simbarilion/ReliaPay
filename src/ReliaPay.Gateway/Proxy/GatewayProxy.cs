using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using ReliaPay.Contracts;
using ReliaPay.Contracts.Envelopes;
using ReliaPay.Gateway.Configuration;
using ReliaPay.Observability;

namespace ReliaPay.Gateway.Proxy;

/// <summary>
/// Проксирует whitelist-запросы на внутренний api по Compose DNS.
/// Не читает JWT, catalog и PostgreSQL: метод, путь, контрактные заголовки и серверный X-Correlation-Id.
/// </summary>
public sealed class GatewayProxy
{
    private static readonly string[] ForwardedRequestHeaders =
    [
        "Authorization",
        "X-Action-Version",
        "Idempotency-Key",
        "X-Provider-Signature",
    ];

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly GatewayOptions _options;

    /// <summary>
    /// Создаёт прокси к базовому URL внутреннего runtime.
    /// </summary>
    /// <param name="httpClient">Клиент с timeout длиннее manifest <c>timeout_ms</c>.</param>
    /// <param name="options">Адрес api внутри сети Compose.</param>
    public GatewayProxy(HttpClient httpClient, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>
    /// Пересылает запрос на api и копирует статус и тело ответа клиенту.
    /// </summary>
    /// <param name="context">Входящий HTTP-контекст шлюза.</param>
    /// <param name="cancellationToken">Токен отмены клиентского запроса.</param>
    /// <returns>Задача завершения проксирования.</returns>
    public async Task ProxyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using Activity? activity = ReliaPayInstrumentation.ActivitySource.StartActivity("gateway.proxy");
        RequestTrace? trace = ReliaPayTraceHttp.Get(context);
        string path = context.Request.Path.Value ?? "/";
        string target = path + context.Request.QueryString.Value;
        if (trace is not null)
        {
            ReliaPayActivity.ApplyTags(activity, trace);
        }
        else
        {
            activity?.SetTag("http.route", path);
        }

        activity?.SetTag("peer.service", "api");

        using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), new Uri(_options.ApiBaseUrl, target));
        CopyContractHeaders(context.Request.Headers, request);
        if (trace is not null)
        {
            request.Headers.TryAddWithoutValidation(
                RequestTraceFactory.CorrelationHeaderName,
                trace.CorrelationId.ToString("D"));
        }
        if (ShouldSendBody(context.Request))
        {
            StreamContent content = new(context.Request.Body);
            CopyContentType(context.Request.ContentType, content);
            request.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteDependencyUnavailableAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteActionTimeoutAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }

            await response.Content.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Копирует только контрактные заголовки. Host, Cookie и прочие hop-by-hop не переносятся.
    /// </summary>
    private static void CopyContractHeaders(IHeaderDictionary source, HttpRequestMessage target)
    {
        foreach (string name in ForwardedRequestHeaders)
        {
            if (!source.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues values)
                || values.Count == 0)
            {
                continue;
            }

            target.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    /// <summary>
    /// Переносит Content-Type без изменения media type и charset.
    /// </summary>
    private static void CopyContentType(string? contentType, HttpContent content)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return;
        }

        if (MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed))
        {
            content.Headers.ContentType = parsed;
            return;
        }

        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }

    /// <summary>
    /// GET/HEAD OpenAPI и health не несут тело; POST action пересылает payload как есть.
    /// </summary>
    private static bool ShouldSendBody(HttpRequest request)
    {
        return !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
    }

    /// <summary>
    /// 504, когда api не успел ответить в бюджете шлюза. Не подменяет недоступность зависимости.
    /// </summary>
    private static Task WriteActionTimeoutAsync(HttpContext context, CancellationToken cancellationToken)
    {
        Guid? correlationId = ReliaPayTraceHttp.Get(context)?.CorrelationId;
        HttpErrorEnvelope envelope = HttpErrorEnvelope.Create(
            ErrorCodes.ActionTimeout,
            "action timed out",
            retryable: true,
            correlationId: correlationId,
            actionVersion: null);
        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        return context.Response.WriteAsJsonAsync(envelope, ResponseJsonOptions, cancellationToken);
    }

    /// <summary>
    /// 503 без внутреннего DNS и адресов, чтобы граница доверия не утекала клиенту.
    /// </summary>
    private static Task WriteDependencyUnavailableAsync(HttpContext context, CancellationToken cancellationToken)
    {
        Guid? correlationId = ReliaPayTraceHttp.Get(context)?.CorrelationId;
        HttpErrorEnvelope envelope = HttpErrorEnvelope.Create(
            ErrorCodes.DependencyUnavailable,
            "internal API is unavailable",
            retryable: true,
            correlationId: correlationId,
            actionVersion: null);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return context.Response.WriteAsJsonAsync(envelope, ResponseJsonOptions, cancellationToken);
    }
}
