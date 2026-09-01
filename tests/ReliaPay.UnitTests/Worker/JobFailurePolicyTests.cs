using FluentAssertions;
using ReliaPay.Contracts;
using ReliaPay.Worker;

namespace ReliaPay.UnitTests.Worker;

public sealed class JobFailurePolicyTests
{
    [Theory]
    [InlineData(ErrorCodes.ActionTimeout, true)]
    [InlineData(ErrorCodes.DependencyUnavailable, true)]
    [InlineData(ErrorCodes.InternalError, true)]
    [InlineData(ErrorCodes.WorkflowMappingMissing, false)]
    [InlineData(ErrorCodes.PayloadInvalid, false)]
    [InlineData(ErrorCodes.ActionContractViolation, false)]
    [InlineData(ErrorCodes.AccessDenied, false)]
    [InlineData(ErrorCodes.ActionNotFound, false)]
    public void Classifies_fail_job_retryable_flag(string code, bool retryable)
    {
        JobFailurePolicy.IsRetryable(code).Should().Be(retryable);
    }
}
