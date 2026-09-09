using FluentAssertions;
using ReliaPay.Contracts;

namespace ReliaPay.UnitTests.Contracts;

public sealed class HttpErrorMatrixTests
{
    [Theory]
    [InlineData(ErrorCodes.AuthInvalid, 401, false)]
    [InlineData(ErrorCodes.SignatureInvalid, 401, false)]
    [InlineData(ErrorCodes.RequestInvalid, 400, false)]
    [InlineData(ErrorCodes.IdempotencyRequired, 400, false)]
    [InlineData(ErrorCodes.AccessDenied, 403, false)]
    [InlineData(ErrorCodes.ReceiptSignatureRequired, 403, false)]
    [InlineData(ErrorCodes.ActionNotFound, 404, false)]
    [InlineData(ErrorCodes.OperationNotFound, 404, false)]
    [InlineData(ErrorCodes.IdempotencyConflict, 409, false)]
    [InlineData(ErrorCodes.WorkflowDecisionConflict, 409, false)]
    [InlineData(ErrorCodes.PayloadInvalid, 422, false)]
    [InlineData(ErrorCodes.ReceiptExternalRequestNotFound, 422, false)]
    [InlineData(ErrorCodes.DependencyUnavailable, 503, true)]
    [InlineData(ErrorCodes.ActionTimeout, 504, true)]
    [InlineData(ErrorCodes.ActionContractViolation, 500, false)]
    public void Matrix_matches_course_http_table(string code, int status, bool retryable)
    {
        HttpErrorMatrix.StatusCode(code).Should().Be(status);
        HttpErrorMatrix.Retryable(code).Should().Be(retryable);
    }
}
