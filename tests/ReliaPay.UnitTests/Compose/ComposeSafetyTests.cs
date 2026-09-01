using FluentAssertions;

namespace ReliaPay.UnitTests.Compose;

public sealed class ComposeSafetyTests
{
    [Fact]
    public void Root_compose_does_not_bind_mount_the_repository()
    {
        string compose = File.ReadAllText(ComposePath());
        compose.Should().NotContain("./db/init");
        compose.Should().NotContain("bind:");
        compose.Should().Contain("db/postgres/Dockerfile");
        compose.Should().Contain("COURSE_WORKER_PASSWORD");
        compose.Should().Contain("course_data");
        compose.Should().Contain("worker-a:");
        compose.Should().Contain("worker-b:");
        compose.Should().Contain("reliapay-worker:local");
        compose.Should().Contain("COURSE_FAILPOINT");
        compose.Should().Contain("COURSE_WORKER_INSTANCE_ID");
        CountOccurrences(compose, "image: reliapay-worker:local").Should().Be(2);
    }

    [Fact]
    public void Worker_image_is_a_dotnet_runtime()
    {
        string dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReliaPay.Worker", "Dockerfile"));
        dockerfile.Should().Contain("mcr.microsoft.com/dotnet/sdk:10.0@sha256:");
        dockerfile.Should().Contain("mcr.microsoft.com/dotnet/runtime:10.0@sha256:");
        dockerfile.Should().Contain("ReliaPay.Worker");
    }

    [Fact]
    public void Postgres_image_copies_role_bootstrap_into_the_image()
    {
        string dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "db", "postgres", "Dockerfile"));
        dockerfile.Should().Contain("COPY db/init /docker-entrypoint-initdb.d");
        dockerfile.Should().Contain("postgres:16@sha256:");
        dockerfile.Should().Contain("ensure-app-roles.sh");
        dockerfile.Should().Contain("reliapay-pg-entrypoint.sh");
    }

    [Fact]
    public void Empty_volume_bootstrap_creates_workflow_worker_and_schema()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "db", "init", "01-roles.sh"));
        script.Should().Contain("CREATE ROLE workflow_worker");
        script.Should().Contain("CREATE SCHEMA workflow");
        script.Should().Contain("COURSE_WORKER_PASSWORD");
        script.Should().Contain("GRANT CONNECT ON DATABASE");
        script.Should().Contain("GRANT CREATE ON DATABASE");
    }

    [Fact]
    public void Existing_volume_bootstrap_grants_schema_create_to_course_owner()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "db", "postgres", "ensure-app-roles.sh"));
        script.Should().Contain("GRANT CREATE ON DATABASE");
        script.Should().Contain("course_owner");
    }

    private static string ComposePath()
    {
        return Path.Combine(RepoRoot(), "compose.yaml");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "compose.yaml");
            string solution = Path.Combine(directory.FullName, "ReliaPay.sln");
            if (File.Exists(candidate) && File.Exists(solution))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root with compose.yaml was not found.");
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
