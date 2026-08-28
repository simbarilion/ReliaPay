using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Catalog;

public sealed class ActionManifestReaderTests
{
    private static readonly ActionManifestReader Reader = new(new JsonDocumentValidator());

    [Fact]
    public void Valid_manifest_maps_route_and_target()
    {
        ActionManifestReadResult result = Reader.Read(CourseManifestJson.OfficialProbe());
        result.IsSuccess.Should().BeTrue();
        result.Manifest!.RouteKey.Should().Be("catalog.probe");
        result.Manifest.TargetFunction.Should().Be("catalog_probe");
        result.Manifest.Enabled.Should().BeFalse();
        result.Manifest.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Official_course_fields_and_unknown_property_are_accepted()
    {
        ActionManifestReadResult result = Reader.Read(
            CourseManifestJson.OfficialProbe(
                extraProperties: """
                    ,
                    "x-autocheck": { "owner": "bot" }
                    """,
                idempotencyMode: "optional"));

        result.IsSuccess.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Manifest!.IdempotencyMode.Should().Be("optional");
    }

    [Fact]
    public void File_enabled_flag_does_not_activate_on_read()
    {
        ActionManifestReadResult result = Reader.Read(
            CourseManifestJson.OfficialProbe(
                extraProperties: """
                    ,
                    "owner": "autocheck"
                    """).Replace("\"enabled\": false", "\"enabled\": true"));

        result.IsSuccess.Should().BeTrue();
        result.Manifest!.Enabled.Should().BeFalse();
        result.Manifest.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void Get_method_is_rejected()
    {
        Reader.Read(CourseManifestJson.OfficialProbe(httpMethod: "GET"))
            .ErrorCode.Should().Be(ErrorCodes.ManifestInvalid);
    }
}
