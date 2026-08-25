using System.Net;
using System.Text.Json;
using FluentAssertions;
using ReliaPay.TestSupport;

namespace ReliaPay.StressTests;

public sealed class GatewayStressTests : IAsyncLifetime
{
    private readonly GatewayTestClient _gateway = new();
    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        await _gateway.WaitUntilReadyAsync();
        _token = JwtTestToken.MintCandidate();
    }

    public Task DisposeAsync()
    {
        _gateway.Dispose();
        return Task.CompletedTask;
    }

    [Fact(Timeout = 120_000)]
    public async Task Parallel_unique_payments_do_not_return_5xx()
    {
        const int count = 32;
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, count)
            .Select(index => _gateway.PostActionAsync(
                "payment",
                "request",
                """{"operationKind":"PAYMENT_EXECUTION","amount":"1.00","currency":"RUB"}""",
                _token,
                $"stress-{index}-{Guid.NewGuid():N}"))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        try
        {
            responses.Should().HaveCount(count);
            responses.Should().OnlyContain(response => (int)response.StatusCode < 500);
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            foreach (HttpResponseMessage response in responses)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                document.RootElement.GetProperty("status").GetString().Should().Be("ok");
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }
}
