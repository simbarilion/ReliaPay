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

    [Fact]
    public void Flow_validate_and_publish_take_map_path()
    {
        CliCommandParser.Parse(["flow", "validate", "/autocheck/input/map.json"])
            .Should().BeOfType<FlowValidateCliCommand>()
            .Which.MapPath.Should().Be("/autocheck/input/map.json");
        CliCommandParser.Parse(["flow", "publish", "/dev/stdin"])
            .Should().BeOfType<FlowPublishCliCommand>()
            .Which.MapPath.Should().Be("/dev/stdin");
    }

    [Fact]
    public void Flow_list_has_no_extra_args()
    {
        CliCommandParser.Parse(["flow", "list"]).Should().BeOfType<FlowListCliCommand>();
        CliCommandParser.Parse(["flow", "list", "extra"]).Should().BeOfType<InvalidCliCommand>();
    }

    [Fact]
    public void Flow_activate_reads_name_and_version()
    {
        FlowActivateCliCommand activate = CliCommandParser.Parse(
                ["flow", "activate", "flow-4a31f70549", "--version", "2"])
            .Should().BeOfType<FlowActivateCliCommand>().Subject;
        activate.FlowName.Should().Be("flow-4a31f70549");
        activate.Version.Should().Be(2);
    }

    [Fact]
    public void Flow_start_reads_business_key_and_data()
    {
        FlowStartCliCommand start = CliCommandParser.Parse(
                [
                    "flow", "start", "flow-4a31f70549",
                    "--business-key", "bk-1",
                    "--data", "/autocheck/input/data.json"
                ])
            .Should().BeOfType<FlowStartCliCommand>().Subject;
        start.FlowName.Should().Be("flow-4a31f70549");
        start.BusinessKey.Should().Be("bk-1");
        start.DataPath.Should().Be("/autocheck/input/data.json");
    }

    [Fact]
    public void Flow_start_without_data_uses_empty_object()
    {
        FlowStartCliCommand start = CliCommandParser.Parse(
                ["flow", "start", "flow-4a31f70549", "--business-key", "bk-1"])
            .Should().BeOfType<FlowStartCliCommand>().Subject;
        start.DataPath.Should().BeEmpty();
    }

    [Fact]
    public void Flow_get_and_signal_read_process_id()
    {
        CliCommandParser.Parse(["flow", "get", "442e3810-49b0-46a0-9938-05cd02cab286"])
            .Should().BeOfType<FlowGetCliCommand>()
            .Which.ProcessId.Should().Be("442e3810-49b0-46a0-9938-05cd02cab286");

        FlowSignalCliCommand signal = CliCommandParser.Parse(
                [
                    "flow", "signal", "442e3810-49b0-46a0-9938-05cd02cab286",
                    "--type", "stage2.received",
                    "--message-id", "msg-1",
                    "--payload", "/autocheck/input/signal.json"
                ])
            .Should().BeOfType<FlowSignalCliCommand>().Subject;
        signal.SignalType.Should().Be("stage2.received");
        signal.MessageId.Should().Be("msg-1");
        signal.PayloadPath.Should().Be("/autocheck/input/signal.json");
    }

    [Fact]
    public void Flow_test_finish_is_available_only_in_the_test_profile()
    {
        CliCommandParser.Parse(
                [
                    "flow", "test-finish", "f3abf6d2-ad3b-4928-b89f-3db379fbe28b",
                    "--owner", "worker-a",
                    "--lease-version", "1",
                    "--outcome", "CREATED",
                    "--result", "/autocheck/input/result.json"
                ],
                courseTestProfile: "1")
            .Should().BeOfType<FlowTestFinishCliCommand>()
            .Which.LeaseVersion.Should().Be(1);

        CliCommandParser.Parse(
                ["flow", "test-finish", "f3abf6d2-ad3b-4928-b89f-3db379fbe28b"],
                courseTestProfile: null)
            .Should().BeOfType<InvalidCliCommand>();
    }
}
