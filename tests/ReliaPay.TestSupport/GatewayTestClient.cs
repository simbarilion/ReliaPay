using System.Net.Http.Headers;
using System.Text;

namespace ReliaPay.TestSupport;

public sealed class GatewayTestClient : IDisposable
{
    public GatewayTestClient()
    {
        string baseUrl = Environment.GetEnvironmentVariable("COURSE_GATEWAY_BASE_URL") ?? "http://127.0.0.1:8080";
        Http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public HttpClient Http { get; }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt < 45; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync("health/ready", cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"gateway is not ready at {Http.BaseAddress}");
    }

    public async Task<HttpResponseMessage> PostActionAsync(
        string module,
        string action,
        string json,
        string? bearerToken,
        string? idempotencyKey,
        string? actionVersion = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"api/{module}/{action}");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (!string.IsNullOrWhiteSpace(actionVersion))
        {
            request.Headers.TryAddWithoutValidation("X-Action-Version", actionVersion);
        }

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Http.SendAsync(request).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Http.Dispose();
    }
}
