using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class PaymentSubmitBoundaryTests
{
    private const string Migration = "20260909020000_payment_submit_and_maps.sql";

    [Fact]
    public void Migration_publishes_submit_internal_actions_and_both_payment_maps()
    {
        string sql = ReadRepoFile("db", "migrations", Migration);

        sql.Should().Contain("CREATE FUNCTION app.payment_submit(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.operation_events(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_validate(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_prepare_external(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_apply_receipt(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_complete(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_reject(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_check_limit(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("CREATE FUNCTION app.payment_approve(p_context jsonb, p_payload jsonb)");

        sql.Should().Contain("\"action\": \"submit\"");
        sql.Should().Contain("\"action\": \"events\"");
        sql.Should().Contain("\"action\": \"validate\"");
        sql.Should().Contain("\"action\": \"prepare_external\"");
        sql.Should().Contain("\"action\": \"apply_receipt\"");
        sql.Should().Contain("\"action\": \"complete\"");
        sql.Should().Contain("\"action\": \"reject\"");
        sql.Should().Contain("\"action\": \"check_limit\"");
        sql.Should().Contain("\"action\": \"approve\"");

        sql.Should().Contain("\"required_policy\": [\"payment:write\"]");
        sql.Should().Contain("\"required_policy\": [\"payment:read\"]");
        sql.Should().Contain("\"required_policy\": [\"payment:internal\"]");
        sql.Should().Contain("PAYMENT_EXECUTION");
        sql.Should().Contain("payment-processing");
        sql.Should().Contain("PAYMENT_APPROVAL");
        sql.Should().Contain("payment-review");
        sql.Should().Contain("'PROCESSING'");
        sql.Should().Contain("OPERATION_SUBMITTED");
        sql.Should().Contain("workflow.start_process");
        sql.Should().Contain("api.activate_action('payment.submit', 1)");
        sql.Should().Contain("workflow.activate_flow('payment-processing', 1)");
        sql.Should().Contain("workflow.activate_flow('payment-review', 1)");
        sql.Should().Contain("REVOKE ALL ON FUNCTION app.payment_submit(jsonb, jsonb) FROM PUBLIC");
        sql.Should().Contain("ON CONFLICT (external_request_id) DO NOTHING");
        sql.Should().Contain("course-limit-v1");
        sql.Should().Contain("100000.00");
        sql.Should().Contain("'LIMIT_RULE'");
        sql.Should().Contain("'WITHIN_LIMIT'");
        sql.Should().Contain("'REVIEW_REQUIRED'");
    }

    [Fact]
    public void Bundled_submit_contract_and_maps_match_week_three_binding()
    {
        string submit = ReadRepoFile("db", "manifests", "payment.submit.v1.json");
        submit.Should().Contain("\"module\": \"payment\"");
        submit.Should().Contain("\"action\": \"submit\"");
        submit.Should().Contain("\"target_function\": \"payment_submit\"");
        submit.Should().Contain("\"payment:write\"");
        submit.Should().Contain("\"idempotency_mode\": \"required\"");
        submit.Should().Contain("payment-processing");
        submit.Should().Contain("payment-review");
        submit.Should().Contain("\"const\": \"PROCESSING\"");

        string events = ReadRepoFile("db", "manifests", "operation.events.v1.json");
        events.Should().Contain("\"module\": \"operation\"");
        events.Should().Contain("\"action\": \"events\"");
        events.Should().Contain("\"payment:read\"");

        string validate = ReadRepoFile("db", "manifests", "payment.validate.v1.json");
        validate.Should().Contain("\"payment:internal\"");
        validate.Should().Contain("\"VALID\"");

        string prepare = ReadRepoFile("db", "manifests", "payment.prepare_external.v1.json");
        prepare.Should().Contain("\"action\": \"prepare_external\"");
        prepare.Should().Contain("\"PREPARED\"");

        string apply = ReadRepoFile("db", "manifests", "payment.apply_receipt.v1.json");
        apply.Should().Contain("\"COMPLETED\"");
        apply.Should().Contain("\"REJECTED\"");

        string processing = ReadRepoFile("db", "maps", "payment-processing.v1.json");
        processing.Should().Contain("\"flow_name\": \"payment-processing\"");
        processing.Should().Contain("\"version\": 1");
        processing.Should().Contain("\"action\": \"validate\"");
        processing.Should().Contain("\"action\": \"prepare_external\"");
        processing.Should().Contain("\"signal_type\": \"payment.receipt\"");
        processing.Should().Contain("\"action\": \"apply_receipt\"");
        processing.Should().Contain("\"service\": \"postgres\"");
        processing.Should().Contain("\"payment:internal\"");

        string issuedYaml = ReadRepoFile("db", "maps", "payment-processing.v1.yaml");
        issuedYaml.Should().Contain("flow_name: payment-processing");
        issuedYaml.Should().Contain("signal_type: payment.receipt");
        issuedYaml.Should().Contain("action: prepare_external");
        issuedYaml.Should().Contain("outcome: FINISHED");

        string review = ReadRepoFile("db", "maps", "payment-review.v1.json");
        review.Should().Contain("\"flow_name\": \"payment-review\"");
        review.Should().Contain("\"action\": \"check_limit\"");
        review.Should().Contain("\"WITHIN_LIMIT\"");
        review.Should().Contain("\"REVIEW_REQUIRED\"");
        review.Should().Contain("\"type\": \"manual\"");
        review.Should().Contain("\"APPROVED\"");
        review.Should().Contain("\"REJECTED\"");
        review.Should().Contain("\"action\": \"approve\"");

        string payloadSchema = ReadRepoFile("contracts", "course-1", "payment-submit.payload.schema.json");
        payloadSchema.Should().Contain("operationId");
        payloadSchema.Should().Contain("additionalProperties");

        string resultSchema = ReadRepoFile("contracts", "course-1", "payment-submit.result.schema.json");
        resultSchema.Should().Contain("processId");
        resultSchema.Should().Contain("payment-processing");
        resultSchema.Should().Contain("\"const\": \"PROCESSING\"");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Repository file {string.Join('/', parts)} was not found.");
    }
}
