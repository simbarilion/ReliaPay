using System.Text.Json;
using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.Execution;

namespace ReliaPay.UnitTests.Execution;

public sealed class WorkflowInputMapperTests
{
    [Fact]
    public void Maps_source_pointer_onto_destination()
    {
        using JsonDocument data = JsonDocument.Parse("""{"value":"hello","other":1}""");
        bool ok = WorkflowInputMapper.TryMap(
            data.RootElement,
            new Dictionary<string, string> { ["/value"] = "/value" },
            JsonDocument.Parse("{}").RootElement,
            out string payload,
            out string? error);
        ok.Should().BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("value").GetString().Should().Be("hello");
        mapped.RootElement.TryGetProperty("other", out _).Should().BeFalse();
    }

    [Fact]
    public void Missing_source_pointer_is_mapping_missing()
    {
        using JsonDocument data = JsonDocument.Parse("""{"value":"hello"}""");
        bool ok = WorkflowInputMapper.TryMap(
            data.RootElement,
            new Dictionary<string, string> { ["/need"] = "/absent" },
            JsonDocument.Parse("{}").RootElement,
            out _,
            out string? error);
        ok.Should().BeFalse();
        error.Should().Be(ErrorCodes.WorkflowMappingMissing);
    }

    [Fact]
    public void Constants_are_written_to_payload()
    {
        using JsonDocument data = JsonDocument.Parse("{}");
        using JsonDocument constants = JsonDocument.Parse("""{"marker":"x"}""");
        WorkflowInputMapper.TryMap(
                data.RootElement,
                new Dictionary<string, string>(),
                constants.RootElement,
                out string payload,
                out string? error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("marker").GetString().Should().Be("x");
    }

    [Fact]
    public void Nested_destination_is_created_from_source_pointer()
    {
        using JsonDocument data = JsonDocument.Parse("""{"subject":{"name":"ada"}}""");
        WorkflowInputMapper.TryMap(
                data.RootElement,
                new Dictionary<string, string> { ["/person/name"] = "/subject/name" },
                JsonDocument.Parse("{}").RootElement,
                out string payload,
                out string? error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("person").GetProperty("name").GetString().Should().Be("ada");
    }

    [Fact]
    public void Constant_slash_name_is_a_literal_property_not_a_nested_path()
    {
        using JsonDocument data = JsonDocument.Parse("{}");
        using JsonDocument constants = JsonDocument.Parse("""{"a/b":"leaf"}""");
        WorkflowInputMapper.TryMap(
                data.RootElement,
                new Dictionary<string, string>(),
                constants.RootElement,
                out string payload,
                out string? error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("a/b").GetString().Should().Be("leaf");
        mapped.RootElement.TryGetProperty("a", out _).Should().BeFalse();
    }

    [Fact]
    public void Constant_tilde_name_is_written_without_treating_tilde_as_escape()
    {
        using JsonDocument data = JsonDocument.Parse("{}");
        using JsonDocument constants = JsonDocument.Parse("""{"a~b":"tilde"}""");
        WorkflowInputMapper.TryMap(
                data.RootElement,
                new Dictionary<string, string>(),
                constants.RootElement,
                out string payload,
                out string? error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("a~b").GetString().Should().Be("tilde");
    }

    [Fact]
    public void Escaped_mapping_destination_writes_slash_property()
    {
        using JsonDocument data = JsonDocument.Parse("""{"value":"hello"}""");
        WorkflowInputMapper.TryMap(
                data.RootElement,
                new Dictionary<string, string> { ["/a~1b"] = "/value" },
                JsonDocument.Parse("{}").RootElement,
                out string payload,
                out string? error)
            .Should()
            .BeTrue();
        error.Should().BeNull();
        using JsonDocument mapped = JsonDocument.Parse(payload);
        mapped.RootElement.GetProperty("a/b").GetString().Should().Be("hello");
    }
}
