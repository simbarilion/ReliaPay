using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ReliaPay.Security;

namespace ReliaPay.UnitTests.Security;

public sealed class ProviderHmacTests
{
    [Fact]
    public void Absent_header_is_not_a_signature_failure()
    {
        byte[] key = Encoding.UTF8.GetBytes("raw-secret-without-trim");
        ProviderHmac.Verify(key, "v1=deadbeef"u8.ToArray(), header: null)
            .Should()
            .Be(ProviderHmacResult.Absent);
        ProviderHmac.Verify(key, "{}"u8.ToArray(), header: "")
            .Should()
            .Be(ProviderHmacResult.Absent);
    }

    [Fact]
    public void Verifies_hmac_over_exact_body_bytes_with_raw_utf8_secret()
    {
        byte[] key = Encoding.UTF8.GetBytes(" leading-space-and-not-base64 ");
        byte[] body = """{"messageId":"p-1","version":1}"""u8.ToArray();
        string header = "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant();

        ProviderHmac.Verify(key, body, header).Should().Be(ProviderHmacResult.Verified);

        byte[] trimmedKey = Encoding.UTF8.GetBytes("leading-space-and-not-base64");
        ProviderHmac.Verify(trimmedKey, body, header).Should().Be(ProviderHmacResult.Invalid);
    }

    [Fact]
    public void Rejects_uppercase_hex_wrong_version_and_mismatched_digest()
    {
        byte[] key = Encoding.UTF8.GetBytes("course-hmac-secret-at-least-32-bytes!!");
        byte[] body = "{}"u8.ToArray();
        string lower = "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant();
        string upper = "v1=" + Convert.ToHexString(HMACSHA256.HashData(key, body));

        ProviderHmac.Verify(key, body, upper).Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify(key, body, "v2=" + lower[3..]).Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify(key, body, "v1=" + new string('0', 64)).Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify(key, body, "v1=not-hex").Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify(key, body, lower + " ").Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify([], body, lower).Should().Be(ProviderHmacResult.Invalid);
        ProviderHmac.Verify(key, "{ }"u8.ToArray(), lower).Should().Be(ProviderHmacResult.Invalid);
    }

    [Fact]
    public void Header_name_is_generic_provider_signature()
    {
        ProviderHmac.HeaderName.Should().Be("X-Provider-Signature");
    }
}
