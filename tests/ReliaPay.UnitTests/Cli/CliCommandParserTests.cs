using FluentAssertions;
using ReliaPay.Cli;

namespace ReliaPay.UnitTests.Cli;

public sealed class CliCommandParserTests
{
    [Fact]
    public void Empty_args_are_idle()
    {
        CliCommandParser.Parse([]).Should().BeOfType<IdleCliCommand>();
    }

    [Fact]
    public void Migration_apply_requires_directory()
    {
        ParsedCliCommand command = CliCommandParser.Parse(["migration", "apply", "/migrations"]);
        command.Should().BeOfType<MigrationApplyCliCommand>()
            .Which.Directory.Should().Be("/migrations");
    }

    [Fact]
    public void Action_validate_and_publish_take_manifest_path()
    {
        CliCommandParser.Parse(["action", "validate", "/autocheck/input/a.json"])
            .Should().BeOfType<ActionValidateCliCommand>()
            .Which.ManifestPath.Should().Be("/autocheck/input/a.json");
        CliCommandParser.Parse(["action", "publish", "/autocheck/input/a.json"])
            .Should().BeOfType<ActionPublishCliCommand>();
    }

    [Fact]
    public void Action_list_has_no_extra_args()
    {
        CliCommandParser.Parse(["action", "list"]).Should().BeOfType<ActionListCliCommand>();
        CliCommandParser.Parse(["action", "list", "extra"]).Should().BeOfType<InvalidCliCommand>();
    }

    [Fact]
    public void Activate_reads_route_and_version_flag()
    {
        ParsedCliCommand command = CliCommandParser.Parse(
            ["action", "activate", "catalog.probe", "--version", "2"]);
        ActionActivateCliCommand activate = command.Should().BeOfType<ActionActivateCliCommand>().Subject;
        activate.Route.Should().Be("catalog.probe");
        activate.Version.Should().Be(2);
    }

    [Fact]
    public void Disable_reads_optional_replacement()
    {
        ActionDisableCliCommand without = CliCommandParser.Parse(
                ["action", "disable", "catalog.probe", "--version", "1"])
            .Should().BeOfType<ActionDisableCliCommand>().Subject;
        without.ReplacementVersion.Should().BeNull();

        ActionDisableCliCommand with = CliCommandParser.Parse(
                ["action", "disable", "catalog.probe", "--version=1", "--replacement-version", "2"])
            .Should().BeOfType<ActionDisableCliCommand>().Subject;
        with.Version.Should().Be(1);
        with.ReplacementVersion.Should().Be(2);
    }

    [Fact]
    public void Unknown_command_is_request_invalid()
    {
        ParsedCliCommand command = CliCommandParser.Parse(["explode"]);
        command.Should().BeOfType<InvalidCliCommand>()
            .Which.Code.Should().Be("request.invalid");
    }
}
