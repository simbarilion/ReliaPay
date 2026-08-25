using FluentAssertions;
using ReliaPay.Data.Migrations;

namespace ReliaPay.UnitTests.Migrations;

public sealed class MigrationChecksumTests
{
    [Fact]
    public void Empty_payload_matches_published_sha256()
    {
        string hex = MigrationChecksum.ComputeSha256Hex(ReadOnlySpan<byte>.Empty);
        hex.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        hex.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}

public sealed class MigrationScriptLoaderTests
{
    [Fact]
    public void Load_orders_sql_files_lexicographically_by_name()
    {
        string directory = Directory.CreateTempSubdirectory("reliapay-migrations-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "02_second.sql"), "SELECT 2;");
            File.WriteAllText(Path.Combine(directory, "01_first.sql"), "SELECT 1;");
            IReadOnlyList<MigrationScript> scripts = MigrationScriptLoader.Load(directory);
            scripts.Select(static script => script.FileName).Should().Equal("01_first.sql", "02_second.sql");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class MigrationApplierTests
{
    [Fact]
    public async Task Apply_executes_new_file_and_skips_identical_checksum()
    {
        string directory = Directory.CreateTempSubdirectory("reliapay-apply-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "01_a.sql"), "SELECT 1;");
            InMemoryMigrationJournal journal = new();
            MigrationApplier applier = new(journal);

            MigrationApplyResult first = await applier.ApplyAsync(directory, CancellationToken.None);
            first.AppliedCount.Should().Be(1);
            first.SkippedCount.Should().Be(0);
            journal.Executed.Should().HaveCount(1);

            MigrationApplyResult second = await applier.ApplyAsync(directory, CancellationToken.None);
            second.AppliedCount.Should().Be(0);
            second.SkippedCount.Should().Be(1);
            journal.Executed.Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_conflicts_when_applied_file_bytes_change()
    {
        string directory = Directory.CreateTempSubdirectory("reliapay-conflict-").FullName;
        try
        {
            string path = Path.Combine(directory, "01_a.sql");
            File.WriteAllText(path, "SELECT 1;");
            InMemoryMigrationJournal journal = new();
            MigrationApplier applier = new(journal);
            await applier.ApplyAsync(directory, CancellationToken.None);

            File.WriteAllText(path, "SELECT 2;");
            Func<Task> act = async () => await applier.ApplyAsync(directory, CancellationToken.None);
            await act.Should().ThrowAsync<MigrationConflictException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class InMemoryMigrationJournal : IMigrationJournal
    {
        private readonly Dictionary<string, string> _checksums = new(StringComparer.Ordinal);

        public List<string> Executed { get; } = [];

        public Task<string?> GetChecksumAsync(string fileName, CancellationToken cancellationToken)
        {
            return Task.FromResult(_checksums.TryGetValue(fileName, out string? checksum) ? checksum : null);
        }

        public Task ApplyInTransactionAsync(MigrationScript script, CancellationToken cancellationToken)
        {
            Executed.Add(script.FileName);
            _checksums[script.FileName] = script.Checksum;
            return Task.CompletedTask;
        }
    }
}
