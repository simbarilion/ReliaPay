using FluentAssertions;
using ReliaPay.Contracts;

namespace ReliaPay.StressTests;

public sealed class SkeletonStressTests
{
    [Fact]
    public void Stress_host_binds_to_course_contract_version()
    {
        CourseContracts.Version.Should().Be("course-1");
    }
}
