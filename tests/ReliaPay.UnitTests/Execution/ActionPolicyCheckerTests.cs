using FluentAssertions;
using ReliaPay.Execution;
using ReliaPay.Security;

namespace ReliaPay.UnitTests.Execution;

public sealed class ActionPolicyCheckerTests
{
    [Fact]
    public void Policy_requires_every_manifest_scope()
    {
        JwtPrincipal principal = new(true, "candidate-client", "web", ["payment:read"]);
        ActionPolicyChecker.IsSatisfied(["payment:write"], principal).Should().BeFalse();
        ActionPolicyChecker.IsSatisfied(["payment:read"], principal).Should().BeTrue();
    }
}
