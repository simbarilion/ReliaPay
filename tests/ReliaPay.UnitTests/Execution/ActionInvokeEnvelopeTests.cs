using System.Text.Json;
using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Contracts;
using ReliaPay.Data;
using ReliaPay.Execution;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Execution;

public sealed class ActionInvokeEnvelopeTests
{
    private static readonly JsonDocumentValidator Validator = new();

    [Fact]
    public void Ok_envelope_with_known_outcome_and_valid_result_is_success()
    {
        ActionInvokeParseResult parsed = ActionInvokeEnvelope.Parse(
            """{"status":"ok","outcome":"APPLIED","result":{"value":"ok"}}""",
            Manifest(),
            Validator);

        ActionInvokeParseResult.Success success = parsed.Should().BeOfType<ActionInvokeParseResult.Success>().Subject;
        success.Outcome.Should().Be("APPLIED");
        success.Result.GetProperty("value").GetString().Should().Be("ok");
    }

    [Fact]
    public void Error_envelope_is_failure_with_source_code()
    {
        ActionInvokeParseResult parsed = ActionInvokeEnvelope.Parse(
            """{"status":"error","code":"payload.invalid","message":"payload does not match schema"}""",
            Manifest(),
            Validator);

        ActionInvokeParseResult.Failure failure = parsed.Should().BeOfType<ActionInvokeParseResult.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.PayloadInvalid);
        failure.Message.Should().Be("payload does not match schema");
        failure.Retryable.Should().BeFalse();
    }

    [Fact]
    public void Retryable_error_envelope_is_failure_with_retryable_true()
    {
        ActionInvokeParseResult parsed = ActionInvokeEnvelope.Parse(
            """{"status":"error","code":"fixture.retry","message":"forced","retryable":true}""",
            Manifest(),
            Validator);

        ActionInvokeParseResult.Failure failure = parsed.Should().BeOfType<ActionInvokeParseResult.Failure>().Subject;
        failure.Code.Should().Be("fixture.retry");
        failure.Retryable.Should().BeTrue();
    }

    [Fact]
    public void Unknown_outcome_is_contract_violation()
    {
        ActionInvokeParseResult parsed = ActionInvokeEnvelope.Parse(
            """{"status":"ok","outcome":"OTHER","result":{"value":"ok"}}""",
            Manifest(),
            Validator);

        parsed.Should().BeOfType<ActionInvokeParseResult.Failure>()
            .Which.Code.Should().Be(ErrorCodes.ActionContractViolation);
    }

    [Fact]
    public void Executor_invokes_on_caller_transaction_without_own_commit()
    {
        typeof(ActionExecutor).GetMethod(nameof(ActionExecutor.InvokeOnConnectionAsync))
            .Should()
            .NotBeNull();
        typeof(ActionExecutor).GetConstructor(
                new[] { typeof(PostgresConnectionFactory), typeof(JsonDocumentValidator) })
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void Result_failing_response_schema_is_contract_violation()
    {
        ActionInvokeParseResult parsed = ActionInvokeEnvelope.Parse(
            """{"status":"ok","outcome":"APPLIED","result":{"value":1}}""",
            Manifest(),
            Validator);

        parsed.Should().BeOfType<ActionInvokeParseResult.Failure>()
            .Which.Code.Should().Be(ErrorCodes.ActionContractViolation);
    }

    private static ActionManifest Manifest()
    {
        using JsonDocument request = JsonDocument.Parse("""{"type":"object"}""");
        using JsonDocument response = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["value"],
              "properties": { "value": { "type": "string" } }
            }
            """);
        return new ActionManifest(
            "training",
            "canary",
            1,
            "POST",
            "app",
            "training_canary",
            request.RootElement.Clone(),
            response.RootElement.Clone(),
            ["APPLIED"],
            ["workflow:execute"],
            "none",
            "none",
            2000,
            true,
            true);
    }
}
