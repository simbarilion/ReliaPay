using ReliaPay.Gateway.Configuration;
using ReliaPay.Observability;

namespace ReliaPay.Gateway.Health;

/// <summary>
/// Проверяет liveness внутреннего api по Compose DNS. Отсутствие ответа означает,
/// что шлюз жив, но не готов принимать клиентский трафик.
/// </summary>
public sealed class ApiReadinessProbe
{
    private readonly HttpClient _httpClient;
    private readonly Uri _liveEndpoint;

    /// <summary>
    /// Создаёт пробу готовности, которая ходит только на <c>/health/live</c> внутреннего api.
    /// </summary>
    /// <param name="httpClient">Клиент с коротким timeout; базовый адрес задаёт <see cref="GatewayOptions"/>.</param>
    /// <param name="options">Адрес api в внутренней сети, не localhost с хоста.</param>
    public ApiReadinessProbe(HttpClient httpClient, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _liveEndpoint = new Uri(options.ApiBaseUrl, "/health/live");
    }

    /// <summary>
    /// Возвращает истину, если внутренний api ответил успешным кодом на liveness.
    /// Сетевые ошибки и истечение timeout считаются неготовностью, а не падением шлюза.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены входящего health-запроса.</param>
    /// <returns>Истина, если зависимость доступна.</returns>
    public async Task<bool> IsApiLiveAsync(CancellationToken cancellationToken)
    {
        using var activity = ReliaPayInstrumentation.ActivitySource.StartActivity("gateway.probe.api");
        activity?.SetTag("peer.service", "api");
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(_liveEndpoint, cancellationToken)
                .ConfigureAwait(false);
            bool alive = response.IsSuccessStatusCode;
            activity?.SetTag("probe.success", alive);
            return alive;
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
