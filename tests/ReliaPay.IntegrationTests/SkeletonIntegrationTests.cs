using FluentAssertions;
using ReliaPay.Contracts;

namespace ReliaPay.IntegrationTests;

public sealed class SkeletonIntegrationTests
{
    [Fact]
    public void Integration_host_binds_to_course_contract_version()
    {
        CourseContracts.Version.Should().Be("course-1");
    }
}
