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
        JsonNode post = document["paths"]!["/api/payment/request"]!["post"]!;
        JsonNode versionHeader = post["parameters"]!.AsArray()
            .Single(node => node!["name"]!.ToString() == "X-Action-Version")!;
        versionHeader["required"]!.GetValue<bool>().Should().BeFalse();
        JsonNode envelope = post["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        envelope["required"]!.AsArray().Select(node => node!.ToString()).Should().Contain(["status", "outcome", "result", "meta"]);
        envelope["properties"]!["status"]!["const"]!.ToString().Should().Be("ok");
        envelope["properties"]!["outcome"]!["enum"]!.AsArray().Select(node => node!.ToString()).Should().Equal("CREATED");
        envelope["properties"]!["result"]!["type"]!.ToString().Should().Be("object");
        envelope["properties"]!["meta"]!["properties"]!["correlationId"]!["format"]!.ToString().Should().Be("uuid");
    }

    [Fact]
    public void Exact_document_requires_const_action_version_and_full_envelope()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        ActionManifest manifest = new(
            "payment",
            "request",
            2,
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
            false);
        JsonObject document = new OpenApiDocumentFactory().CreateAction(manifest);
        JsonNode post = document["paths"]!["/api/payment/request"]!["post"]!;
        JsonNode versionHeader = post["parameters"]!.AsArray()
            .Single(node => node!["name"]!.ToString() == "X-Action-Version")!;
        versionHeader["required"]!.GetValue<bool>().Should().BeTrue();
        versionHeader["schema"]!["const"]!.GetValue<int>().Should().Be(2);
        JsonNode envelope = post["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        envelope["properties"]!["meta"]!["properties"]!["actionVersion"]!["const"]!.GetValue<int>().Should().Be(2);
    }
}
