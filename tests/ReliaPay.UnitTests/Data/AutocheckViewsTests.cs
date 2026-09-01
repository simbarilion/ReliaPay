using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class AutocheckViewsTests
{
    [Fact]
    public void Course_week_one_projections_remain_available()
    {
        AutocheckViews.Week1.Should().Equal(
            "contract_info",
            "action_definitions",
            "action_dispatches",
            "operations",
            "operation_events");
        AutocheckViews.Week1.Should().HaveCount(5);
    }

    [Fact]
    public void Course_week_two_adds_workflow_evidence_views()
    {
        AutocheckViews.All.Should().Equal(
            "contract_info",
            "action_definitions",
            "action_dispatches",
            "operations",
            "operation_events",
            "flow_versions",
            "processes",
            "steps",
            "jobs",
            "attempts",
            "signals",
            "workflow_events");
        AutocheckViews.All.Should().OnlyHaveUniqueItems();
        AutocheckViews.All.Should().Contain(AutocheckViews.Week1);
    }

    [Fact]
    public void Contract_info_version_matches_published_course_contract()
    {
        CourseContracts.Version.Should().Be("course-1");
        AutocheckViews.ContractInfo.Should().Be("contract_info");
    }
}
