using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.Data;

namespace ReliaPay.UnitTests.Data;

public sealed class AutocheckViewsTests
{
    [Fact]
    public void Course_week_exposes_exactly_five_autocheck_projections()
    {
        AutocheckViews.All.Should().Equal(
            "contract_info",
            "action_definitions",
            "action_dispatches",
            "operations",
            "operation_events");
        AutocheckViews.All.Should().HaveCount(5);
        AutocheckViews.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Contract_info_version_matches_published_course_contract()
    {
        CourseContracts.Version.Should().Be("course-1");
        AutocheckViews.ContractInfo.Should().Be("contract_info");
    }
}
