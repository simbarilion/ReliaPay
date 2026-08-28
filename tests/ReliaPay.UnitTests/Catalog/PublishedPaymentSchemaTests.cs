using System.Text.Json;
using FluentAssertions;

namespace ReliaPay.UnitTests.Catalog;

public sealed class PublishedPaymentSchemaTests
{
    [Fact]
    public void Payment_request_manifest_uses_canonical_amount_currency_and_uuid()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payment.request.v1.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement request = document.RootElement.GetProperty("request_schema").GetProperty("properties");
        request.GetProperty("amount").GetProperty("pattern").GetString().Should().Contain("[1-9]");
        request.GetProperty("currency").GetProperty("const").GetString().Should().Be("RUB");
        request.GetProperty("operationKind").GetProperty("enum").GetArrayLength().Should().Be(2);
        JsonElement response = document.RootElement.GetProperty("response_schema").GetProperty("properties");
        response.GetProperty("operationId").GetProperty("format").GetString().Should().Be("uuid");
        response.GetProperty("status").GetProperty("enum").GetArrayLength().Should().Be(4);
    }
}
