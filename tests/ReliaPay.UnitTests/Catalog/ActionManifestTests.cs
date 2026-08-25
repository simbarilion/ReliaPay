using System.Text.Json;
using FluentAssertions;
using ReliaPay.Catalog;

namespace ReliaPay.UnitTests.Catalog;

public sealed class ActionManifestTests
{
    [Fact]
    public void Route_key_does_not_include_version()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object"}""");
        ActionManifest manifest = new(
            Module: "catalog",
            Action: "probe",
            Version: 2,
            HttpMethod: "POST",
            TargetSchema: "app",
            TargetFunction: "catalog_probe",
            RequestSchema: schema.RootElement.Clone(),
            ResponseSchema: schema.RootElement.Clone(),
            Outcomes: ["PROBED"],
            RequiredPolicy: ["catalog:probe"],
            IdempotencyMode: "none",
            IdempotencyScope: "none",
            TimeoutMs: 1000,
            Enabled: true,
            IsDefault: true);

        manifest.RouteKey.Should().Be("catalog.probe");
        manifest.Version.Should().Be(2);
        manifest.RequiredPolicy.Should().Equal("catalog:probe");
    }
}
