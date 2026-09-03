using FluentAssertions;
using ReliaPay.Observability;

namespace ReliaPay.UnitTests.Execution;

public sealed class ActionExecutorTelemetryTests
{
    [Fact]
    public void Executor_hashes_idempotency_key_before_activity_tags()
    {
        string source = ReadSource("src", "ReliaPay.Execution", "ActionExecutor.cs");
        source.Should().Contain("ReliaPayActivity.ApplyTags");
        source.Should().Contain("RequestIdHash.Sha256Hex(idempotencyKey)");
        RequestIdHash.Sha256Hex("key-9").Should().NotBe("key-9");
    }

    private static string ReadSource(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Source {string.Join('/', parts)} was not found.");
    }
}
