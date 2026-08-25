using FluentAssertions;
using ReliaPay.Contracts.Operations;

namespace ReliaPay.UnitTests.Contracts;

public sealed class OperationContractTests
{
    [Fact]
    public void Operation_result_keeps_server_assigned_id_and_created_status()
    {
        Guid id = Guid.Parse("8c26513d-8441-43ea-b064-3bca8c240052");
        OperationResult result = new(
            id,
            "request-123",
            OperationKinds.PaymentExecution,
            "1000.00",
            "RUB",
            OperationStatuses.Created);

        result.OperationId.Should().Be(id);
        result.Status.Should().Be("CREATED");
        result.OperationKind.Should().Be("PAYMENT_EXECUTION");
        result.Currency.Should().Be("RUB");
    }
}
