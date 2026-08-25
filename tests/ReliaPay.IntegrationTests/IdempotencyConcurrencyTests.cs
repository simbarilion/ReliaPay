using System.Net;
using System.Text.Json;
using FluentAssertions;
using ReliaPay.TestSupport;

namespace ReliaPay.IntegrationTests;

[Collection("Gateway")]
public sealed class IdempotencyConcurrencyTests : IAsyncLifetime
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
    public async Task Concurrent_posts_with_the_same_key_create_one_operation()
    {
        string key = "conc-" + Guid.NewGuid().ToString("N");
        const string payload = """{"operationKind":"PAYMENT_EXECUTION","amount":"33.00","currency":"RUB"}""";
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, 16)
            .Select(_ => _gateway.PostActionAsync("payment", "request", payload, _token, key))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            HashSet<string> ids = [];
            foreach (HttpResponseMessage response in responses)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                ids.Add(document.RootElement.GetProperty("result").GetProperty("operationId").GetString()!);
            }

            ids.Should().HaveCount(1);
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
