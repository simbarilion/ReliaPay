using FluentAssertions;

namespace ReliaPay.UnitTests.Data;

public sealed class ReceiptAcceptBoundaryTests
{
    private const string Migration = "20260909030000_receipt_accept.sql";

    [Fact]
    public void Migration_publishes_receipt_accept_with_signature_and_inbox_rules()
    {
        string sql = ReadRepoFile("db", "migrations", Migration);
        sql.Should().Contain("CREATE FUNCTION app.receipt_accept(p_context jsonb, p_payload jsonb)");
        sql.Should().Contain("receipt.signature_required");
        sql.Should().Contain("signatureVerified");
        sql.Should().Contain("signatureVersion");
        sql.Should().Contain("receipt.external_request_not_found");
        sql.Should().Contain("idempotency.conflict");
        sql.Should().Contain("'RECEIVED'");
        sql.Should().Contain("'DUPLICATE'");
        sql.Should().Contain("'CONFIRMED'");
        sql.Should().Contain("INSERT INTO delivery.inbox");
        sql.Should().Contain("INSERT INTO delivery.receipts");
        sql.Should().Contain("\"action\": \"accept\"");
        sql.Should().Contain("\"required_policy\": [\"receipt:write\"]");
        sql.Should().Contain("api.activate_action('receipt.accept', 1)");
        sql.Should().Contain("REVOKE ALL ON FUNCTION app.receipt_accept(jsonb, jsonb) FROM PUBLIC");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION app.receipt_accept(jsonb, jsonb) TO outbox_dispatcher");
        sql.Should().NotContain("GRANT EXECUTE ON FUNCTION app.receipt_accept(jsonb, jsonb) TO inbox_reconciler");
    }

    [Fact]
    public void Bundled_manifest_and_schema_match_receipt_v1()
    {
        string manifest = ReadRepoFile("db", "manifests", "receipt.accept.v1.json");
        manifest.Should().Contain("\"module\": \"receipt\"");
        manifest.Should().Contain("\"action\": \"accept\"");
        manifest.Should().Contain("\"target_function\": \"receipt_accept\"");
        manifest.Should().Contain("\"receipt:write\"");
        manifest.Should().Contain("\"RECEIVED\"");
        manifest.Should().Contain("\"DUPLICATE\"");
        manifest.Should().Contain("\"idempotency_mode\": \"required\"");
        manifest.Should().Contain("providerPaymentId");
        manifest.Should().Contain("externalRequestId");

        string schema = ReadRepoFile("contracts", "course-1", "receipt-v1.schema.json");
        schema.Should().Contain("urn:course:course-1:receipt-v1");
        schema.Should().Contain("\"const\": 1");
        schema.Should().Contain("COMPLETED");
        schema.Should().Contain("REJECTED");
    }

    [Fact]
    public void Generic_executor_verifies_signature_without_action_name_branch()
    {
        string executor = ReadRepoFile("src", "ReliaPay.Execution", "ActionExecutor.cs");
        executor.Should().Contain("ProviderHmac");
        executor.Should().Contain("payloadBytes");
        executor.Should().Contain("SignatureInvalid");
        executor.Should().Contain("SignatureVerified");
        executor.Should().NotContain("receipt.accept");

        string program = ReadRepoFile("src", "ReliaPay.Api", "Program.cs");
        program.Should().Contain("ReadBytesAsync");
        program.Should().Contain("ProviderHmac");
        program.Should().Contain("providerSignature");
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
