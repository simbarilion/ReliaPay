using FluentAssertions;

namespace ReliaPay.UnitTests.Docs;

public sealed class WeekThreeDocsTests
{
    [Fact]
    public void C4_lists_ten_compose_containers()
    {
        string c4 = ReadRepoFile("docs", "c4-containers.md");
        c4.Should().Contain("`gateway`");
        c4.Should().Contain("`api`");
        c4.Should().Contain("`cli`");
        c4.Should().Contain("`postgres`");
        c4.Should().Contain("`worker-a`");
        c4.Should().Contain("`worker-b`");
        c4.Should().Contain("`outbox-dispatcher`");
        c4.Should().Contain("`receipt-adapter`");
        c4.Should().Contain("`inbox-reconciler`");
        c4.Should().Contain("`provider-simulator`");
        c4.Should().Contain("ADR-0004");
    }

    [Fact]
    public void Adr_0004_describes_python_anti_corruption_and_sql_acl()
    {
        string adr = ReadRepoFile("docs", "adr", "0004-python-anti-corruption.md");
        adr.Should().Contain("антикоррупц");
        adr.Should().Contain("receipt-adapter");
        adr.Should().Contain("outbox_dispatcher");
        adr.Should().Contain("inbox_reconciler");
        adr.Should().Contain("delivery.claim_outbox");
        adr.Should().Contain("не выбирает");
        File.Exists(Path.Combine(RepoRoot(), "docs", "adr", "0001-trust-boundary.md")).Should().BeTrue();
        File.Exists(Path.Combine(RepoRoot(), "docs", "adr", "0002-technical-vs-domain-result.md")).Should().BeTrue();
        File.Exists(Path.Combine(RepoRoot(), "docs", "adr", "0003-worker-lease-transaction.md")).Should().BeTrue();
    }

    [Fact]
    public void Readme_keeps_week_two_literals_and_marks_manual_as_implemented()
    {
        string readme = ReadRepoFile("README.md");
        readme.Should().Contain("http://localhost:8080");
        readme.Should().Contain("docker compose up -d --build");
        readme.Should().Contain("./check.sh");
        readme.Should().Contain("### Python-периметр");
        readme.Should().Contain("### Provider");
        readme.Should().Contain("workflow.manual");
        readme.Should().NotContain("ручное решение — следующий этап");
    }

    [Fact]
    public void Week_three_schemas_are_in_contracts()
    {
        string root = Path.Combine(RepoRoot(), "contracts", "course-1");
        File.Exists(Path.Combine(root, "receipt-v1.schema.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, "provider-v02-callback.schema.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, "provider-v02-payment-request.schema.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, "payment-submit.payload.schema.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, "workflow-manual.payload.schema.json")).Should().BeTrue();
        File.Exists(Path.Combine(root, "workflow-manual.result.schema.json")).Should().BeTrue();
    }

    [Fact]
    public void Python_perimeter_does_not_log_bodies_or_secrets()
    {
        string perimeter = Path.Combine(RepoRoot(), "python", "perimeter");
        foreach (string file in Directory.GetFiles(perimeter, "*.py"))
        {
            string source = File.ReadAllText(file);
            source.Should().NotContain("print(");
            source.Should().NotContain("logging.");
            source.Should().NotContain("print (");
        }

        string adapter = File.ReadAllText(Path.Combine(perimeter, "adapter.py"));
        adapter.Should().Contain("def log_message");
        adapter.Should().Contain("del format, args");
    }

    [Fact]
    public void Runtime_sources_do_not_contain_forbidden_log_literals()
    {
        string[] roots =
        [
            Path.Combine(RepoRoot(), "src"),
            Path.Combine(RepoRoot(), "python", "perimeter"),
        ];
        foreach (string root in roots)
        {
            foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string source = File.ReadAllText(file);
                source.Should().NotContain("Payment completed");
                source.Should().NotContain("Recovery callback");
            }
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "README.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "docs"))
                && Directory.Exists(Path.Combine(directory.FullName, "python")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        string candidate = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException($"Repository file {string.Join('/', parts)} was not found.");
        }

        return File.ReadAllText(candidate);
    }
}
