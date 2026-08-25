using System.Net;
using System.Text.Json;
using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.TestSupport;

namespace ReliaPay.IntegrationTests;

[Collection("Gateway")]
public sealed class GatewayContractTests : IAsyncLifetime
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
    public async Task Health_and_unknown_route_are_served_by_gateway()
    {
        using HttpResponseMessage live = await _gateway.Http.GetAsync("health/live");
        live.StatusCode.Should().Be(HttpStatusCode.OK);
        using HttpResponseMessage swagger = await _gateway.Http.GetAsync("swagger");
        swagger.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await swagger.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task Payment_request_without_jwt_is_401()
    {
        using HttpResponseMessage response = await _gateway.PostActionAsync(
            "payment",
            "request",
            """{"operationKind":"PAYMENT_EXECUTION","amount":"10.00","currency":"RUB"}""",
            bearerToken: null,
            idempotencyKey: "missing-jwt");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.AuthInvalid);
        body.RootElement.GetProperty("meta").GetProperty("correlationId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Denied_principal_is_403()
    {
        using HttpResponseMessage response = await _gateway.PostActionAsync(
            "payment",
            "request",
            """{"operationKind":"PAYMENT_EXECUTION","amount":"10.00","currency":"RUB"}""",
            JwtTestToken.MintDenied(),
            Guid.NewGuid().ToString("D"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task Payment_request_is_idempotent_and_operation_get_reads_it()
    {
        string key = "itest-" + Guid.NewGuid().ToString("N");
        const string payload = """{"operationKind":"PAYMENT_EXECUTION","amount":"21.00","currency":"RUB"}""";
        using HttpResponseMessage first = await _gateway.PostActionAsync("payment", "request", payload, _token, key);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        created.RootElement.GetProperty("outcome").GetString().Should().Be("CREATED");
        string operationId = created.RootElement.GetProperty("result").GetProperty("operationId").GetString()!;

        using HttpResponseMessage replay = await _gateway.PostActionAsync("payment", "request", payload, _token, key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        replayed.RootElement.GetProperty("result").GetProperty("operationId").GetString().Should().Be(operationId);

        using HttpResponseMessage conflict = await _gateway.PostActionAsync(
            "payment",
            "request",
            """{"operationKind":"PAYMENT_APPROVAL","amount":"21.00","currency":"RUB"}""",
            _token,
            key);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        conflictBody.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.IdempotencyConflict);

        using HttpResponseMessage found = await _gateway.PostActionAsync(
            "operation",
            "get",
            $"{{\"operationId\":\"{operationId}\"}}",
            _token,
            idempotencyKey: null);
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument got = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
        got.RootElement.GetProperty("outcome").GetString().Should().Be("FOUND");
    }

    [Fact]
    public async Task Unknown_operation_is_404()
    {
        using HttpResponseMessage response = await _gateway.PostActionAsync(
            "operation",
            "get",
            """{"operationId":"00000000-0000-0000-0000-000000000001"}""",
            _token,
            idempotencyKey: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.OperationNotFound);
    }

    [Fact]
    public async Task OpenApi_default_includes_payment_request()
    {
        using HttpResponseMessage response = await _gateway.Http.GetAsync("openapi/default.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("/api/payment/request");
        json.Should().NotContain("Bearer ");
    }

    [Fact]
    public async Task Published_check_ping_is_callable_without_api_rebuild()
    {
        using HttpResponseMessage response = await _gateway.PostActionAsync(
            "check",
            "ping",
            "{}",
            _token,
            idempotencyKey: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("outcome").GetString().Should().Be("PROBED");
    }
}

[CollectionDefinition("Gateway")]
public sealed class GatewayCollection : ICollectionFixture<GatewayCollectionMarker>
{
}

public sealed class GatewayCollectionMarker
{
}
