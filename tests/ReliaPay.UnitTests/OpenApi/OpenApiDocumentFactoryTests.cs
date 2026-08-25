using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.OpenApi;

namespace ReliaPay.UnitTests.OpenApi;

public sealed class OpenApiDocumentFactoryTests
{
    [Fact]
    public void Default_document_contains_only_supplied_routes()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        ActionManifest manifest = new(
            "payment",
            "request",
            1,
            "POST",
            "app",
            "payment_request",
            schema.RootElement.Clone(),
            schema.RootElement.Clone(),
            ["CREATED"],
            ["payment:write"],
            "required",
            "principal_action",
            5000,
            true,
            true);
        JsonObject document = new OpenApiDocumentFactory().CreateDefault([manifest]);
        document["paths"]!["/api/payment/request"].Should().NotBeNull();
        document["info"]!["version"]!.ToString().Should().Be("course-1");
    }
}
