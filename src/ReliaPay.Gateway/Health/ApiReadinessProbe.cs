using ReliaPay.Gateway.Configuration;
using ReliaPay.Observability;

namespace ReliaPay.Gateway.Health;

/// <summary>
/// Проверяет готовность внутреннего api по Compose DNS. Liveness шлюза от этой пробы не зависит.
/// </summary>
public sealed class ApiReadinessProbe
{
    private readonly HttpClient _httpClient;
    private readonly Uri _readyEndpoint;

    /// <summary>
    /// Создаёт пробу готовности, которая ходит только на <c>/health/ready</c> внутреннего api.
    /// </summary>
    /// <param name="httpClient">Клиент с коротким timeout; базовый адрес задаёт <see cref="GatewayOptions"/>.</param>
    /// <param name="options">Адрес api в внутренней сети, не localhost с хоста.</param>
    public ApiReadinessProbe(HttpClient httpClient, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _readyEndpoint = new Uri(options.ApiBaseUrl, "/health/ready");
    }

    /// <summary>
    /// Возвращает истину, если внутренний api ответил успешным кодом на readiness.
    /// Сетевые ошибки, 503 и истечение timeout считаются неготовностью, а не падением шлюза.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены входящего health-запроса.</param>
    /// <returns>Истина, если зависимость готова принимать трафик.</returns>
    public async Task<bool> IsApiReadyAsync(CancellationToken cancellationToken)
    {
        using var activity = ReliaPayInstrumentation.ActivitySource.StartActivity("gateway.probe.api");
        activity?.SetTag("peer.service", "api");
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(_readyEndpoint, cancellationToken)
                .ConfigureAwait(false);
            bool ready = response.IsSuccessStatusCode;
            activity?.SetTag("probe.success", ready);
            return ready;
        }
        catch (HttpRequestException)
        {
            activity?.SetTag("probe.success", false);
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("probe.success", false);
            return false;
        }
    }
}
