using FluentAssertions;

namespace ReliaPay.UnitTests.Compose;

public sealed class ComposeDispatcherOrderingTests
{
    [Fact]
    public void Both_outbox_dispatchers_start_after_provider_and_receipt_adapter()
    {
        string compose = File.ReadAllText(Path.Combine(RepoRoot(), "compose.yaml"));

        foreach (string service in new[] { "  outbox-dispatcher:", "  outbox-dispatcher-b:" })
        {
            string block = ServiceBlock(compose, service);
            block.Should().Contain("provider-simulator:");
            block.Should().Contain("receipt-adapter:");
            block.Should().Contain("condition: service_started");
            block.Should().Contain("postgres:");
            block.Should().Contain("condition: service_healthy");
        }

        // Ready path must stay PostgreSQL-driven in dispatcher code (not provider).
        string dispatcher = File.ReadAllText(
            Path.Combine(RepoRoot(), "python", "perimeter", "dispatcher.py"));
        dispatcher.Should().Contain("ready_check=lambda: pg_ready(connection)");
        dispatcher.Should().NotContain("provider-simulator");
        dispatcher.Should().NotContain("/health/ready");
    }

    private static string ServiceBlock(string compose, string marker)
    {
        int start = compose.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        int next = start + marker.Length;
        while (next < compose.Length)
        {
            int line = compose.IndexOf('\n', next);
            if (line < 0)
            {
                return compose[start..];
            }

            int content = line + 1;
            if (content + 2 < compose.Length
                && compose[content] == ' '
                && compose[content + 1] == ' '
                && compose[content + 2] != ' '
                && compose[content + 2] != '\n')
            {
                return compose[start..line];
            }

            next = content;
        }

        return compose[start..];
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml"))
                && File.Exists(Path.Combine(directory.FullName, "ReliaPay.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
