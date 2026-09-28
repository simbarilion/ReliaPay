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
        compose.Should().NotContain("env_file:");
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
    public void Root_compose_declares_week_three_python_and_provider_seam()
    {
        string compose = File.ReadAllText(ComposePath());
        compose.Should().Contain("outbox-dispatcher:");
        compose.Should().Contain("receipt-adapter:");
        compose.Should().Contain("inbox-reconciler:");
        compose.Should().Contain("provider-simulator:");
        compose.Should().Contain("127.0.0.1:${COURSE_GATEWAY_PORT:-8080}:8080");
        compose.Should().Contain("COURSE_POSTGRES_PASSWORD");
        compose.Should().Contain("COURSE_MIGRATOR_PASSWORD");
        compose.Should().Contain("COURSE_PUBLISHER_PASSWORD");
        compose.Should().Contain("COURSE_OUTBOX_PASSWORD");
        compose.Should().Contain("COURSE_INBOX_PASSWORD");
        compose.Should().Contain("PROVIDER_HMAC_SECRET: ${PROVIDER_HMAC_SECRET}");
        compose.Should().Contain("PROVIDER_CALLBACK_CAPABILITY: ${PROVIDER_CALLBACK_CAPABILITY}");
        compose.Should().Contain("PROVIDER_CALLBACK_TOKEN: ${PROVIDER_CALLBACK_TOKEN}");
        compose.Should().Contain("COURSE_JWT_SIGNING_KEY: ${COURSE_JWT_SIGNING_KEY}");
        compose.Should().NotContain("moduledev-course-local-hs256-key");
        compose.Should().Contain(
            "ghcr.io/fintech-dev-lab/internship-provider-simulator:v0.2.0@sha256:70e5e0dd9ab8425be84de431ec74516f9bedf5d5529077358e2e2b2037fe0c74");
        compose.Should().Contain("http://receipt-adapter:8082/callbacks/provider-v02/${PROVIDER_CALLBACK_CAPABILITY}");
        // Week 4: dual dispatcher/reconciler share one local Python image (5 services).
        CountOccurrences(compose, "image: reliapay-python-perimeter:local").Should().Be(5);
        compose.Should().Contain("outbox-dispatcher-b:");
        compose.Should().Contain("inbox-reconciler-b:");
        compose.Should().Contain("OUTBOX_OWNER: ${OUTBOX_OWNER:-outbox-dispatcher}");
        compose.Should().Contain("OUTBOX_OWNER: ${OUTBOX_OWNER_B:-outbox-dispatcher-b}");
        CountOccurrences(compose, "PGUSER: outbox_dispatcher").Should().Be(1);
        CountOccurrences(compose, "PGUSER: inbox_reconciler").Should().Be(1);
        AdapterService(compose).Should().NotContain("PG");
        AdapterService(compose).Should().NotContain("DB_");
        AdapterService(compose).Should().NotContain("DATABASE_");
        AdapterService(compose).Should().NotContain("postgres://");
    }

    [Fact]
    public void Python_image_runs_python_312_as_the_main_process()
    {
        string dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "python", "Dockerfile"));
        dockerfile.Should().Contain("python:3.12");
        dockerfile.Should().Contain("CMD [\"python\", \"-m\", \"perimeter\"]");
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
        script.Should().Contain("CREATE ROLE outbox_dispatcher");
        script.Should().Contain("CREATE ROLE inbox_reconciler");
        script.Should().Contain("CREATE SCHEMA delivery");
        script.Should().Contain("COURSE_OUTBOX_PASSWORD");
        script.Should().Contain("COURSE_INBOX_PASSWORD");
        script.Should().Contain("COURSE_PUBLISHER_PASSWORD");
        script.Should().NotContain("GRANT course_owner TO outbox_dispatcher");
        script.Should().NotContain("GRANT course_owner TO inbox_reconciler");
    }

    [Fact]
    public void Existing_volume_bootstrap_grants_schema_create_to_course_owner()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "db", "postgres", "ensure-app-roles.sh"));
        script.Should().Contain("GRANT CREATE ON DATABASE");
        script.Should().Contain("course_owner");
        script.Should().Contain("outbox_dispatcher");
        script.Should().Contain("inbox_reconciler");
        script.Should().Contain("CREATE SCHEMA IF NOT EXISTS delivery");
        script.Should().Contain("COURSE_OUTBOX_PASSWORD");
        script.Should().Contain("COURSE_INBOX_PASSWORD");
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

    private static string AdapterService(string compose)
    {
        const string marker = "  receipt-adapter:";
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
}
