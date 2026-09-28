using FluentAssertions;

namespace ReliaPay.UnitTests.Observability;

public sealed class CourseMetricsCardinalityTests
{
    [Fact]
    public void Course_metrics_sql_exposes_fixed_series_without_id_labels()
    {
        string sql = ReadRepoFile("db", "migrations", "20260928160000_observability.sql");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION app.course_metrics()");
        sql.Should().Contain("workflow_jobs_ready");
        sql.Should().Contain("workflow_job_oldest_age_seconds");
        sql.Should().Contain("workflow_processes_waiting");
        sql.Should().Contain("outbox_pending");
        sql.Should().Contain("outbox_oldest_age_seconds");
        sql.Should().Contain("workflow_failures_total");
        // Aggregation only ? no per-entity labels in the SQL projection.
        sql.Should().NotContain("AS operation_id");
        sql.Should().NotContain("AS job_id");
        sql.Should().NotContain("AS external_request_id");
        sql.Should().NotContain("AS correlation_id");
    }

    [Fact]
    public void Open_metrics_reader_emits_fixed_sample_names_without_label_sets()
    {
        string source = ReadRepoFile("src", "ReliaPay.Api", "Metrics", "CourseMetricsReader.cs");
        source.Should().Contain("(\"workflow_jobs_ready\", \"gauge\", \"workflow_jobs_ready\"");
        source.Should().Contain("(\"workflow_job_oldest_age_seconds\", \"gauge\", \"workflow_job_oldest_age_seconds\"");
        source.Should().Contain("(\"workflow_processes_waiting\", \"gauge\", \"workflow_processes_waiting\"");
        source.Should().Contain("(\"outbox_pending\", \"gauge\", \"outbox_pending\"");
        source.Should().Contain("(\"outbox_oldest_age_seconds\", \"gauge\", \"outbox_oldest_age_seconds\"");
        source.Should().Contain("(\"workflow_failures\", \"counter\", \"workflow_failures_total\"");
        source.Should().NotContain("operationId=");
        source.Should().NotContain("jobId=");
        source.Should().NotContain("{operation");

        string doc = ReadRepoFile("src", "ReliaPay.Api", "Metrics", "OpenMetricsDocument.cs");
        doc.Should().Contain("# EOF");
        doc.Should().Contain("openmetrics-text");
    }

    private static string ReadRepoFile(params string[] parts)
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

        throw new InvalidOperationException($"File {string.Join('/', parts)} was not found.");
    }
}
