using System.Net;
using System.Text.Json;
using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.TestSupport;

namespace ReliaPay.IntegrationTests;

[Collection("Gateway")]
public sealed class GenericRuntimeIdempotencyTests : IAsyncLifetime
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

    [Fact]
    public async Task Published_non_payment_action_replays_without_a_second_effect()
    {
        string key = "generic-" + Guid.NewGuid().ToString("N");
        string token = "tok-" + Guid.NewGuid().ToString("N");
        string payload = $"{{\"token\":\"{token}\"}}";

        using HttpResponseMessage first = await _gateway.PostActionAsync(
            "check",
            "record",
            payload,
            _token,
            key);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        created.RootElement.GetProperty("outcome").GetString().Should().Be("RECORDED");
        int firstSeq = created.RootElement.GetProperty("result").GetProperty("seq").GetInt32();
        firstSeq.Should().BeGreaterThan(0);

        using HttpResponseMessage replay = await _gateway.PostActionAsync(
            "check",
            "record",
            payload,
            _token,
            key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        replayed.RootElement.GetProperty("result").GetProperty("seq").GetInt32().Should().Be(firstSeq);

        using HttpResponseMessage conflict = await _gateway.PostActionAsync(
            "check",
            "record",
            $"{{\"token\":\"other-{token}\"}}",
            _token,
            key);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        conflictBody.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.IdempotencyConflict);
    }
}
