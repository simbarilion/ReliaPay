using FluentAssertions;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class PostgresRolesTests
{
    [Fact]
    public void Worker_role_name_is_the_course_contract_principal()
    {
        PostgresRoles.Worker.Should().Be("workflow_worker");
        PostgresRoles.Worker.Should().NotBe(PostgresRoles.Runtime);
        PostgresRoles.Worker.Should().NotBe(PostgresRoles.Publication);
    }

    [Fact]
    public void Workflow_schema_is_separate_from_week_one_schemas()
    {
        PostgresSchemas.Workflow.Should().Be("workflow");
        PostgresSchemas.All.Should().Contain(PostgresSchemas.Workflow);
        PostgresSchemas.All.Should().Contain(PostgresSchemas.Api);
        PostgresSchemas.All.Should().Contain(PostgresSchemas.App);
        PostgresSchemas.All.Should().Contain(PostgresSchemas.Autocheck);
    }
}
