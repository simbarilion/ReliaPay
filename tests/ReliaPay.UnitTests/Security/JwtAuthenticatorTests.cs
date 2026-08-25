using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ReliaPay.Security;

namespace ReliaPay.UnitTests.Security;

public sealed class JwtAuthenticatorTests
{
    private const string SigningKey = "course-test-signing-key-32bytes-min";
    private readonly JwtAuthenticator _authenticator = new(new JwtOptions("moduledev-course", "moduledev-api", SigningKey));

    [Fact]
    public async Task Accepts_space_delimited_scope_string()
    {
        string token = Sign("""{"sub":"candidate-client","consumer":"web","scope":"payment:write payment:read","iat":1710000000,"exp":4102444800,"iss":"moduledev-course","aud":"moduledev-api"}""");
        JwtPrincipal principal = await _authenticator.AuthenticateAsync("Bearer " + token);
        principal.IsAuthenticated.Should().BeTrue();
        principal.Principal.Should().Be("candidate-client");
        principal.Consumer.Should().Be("web");
        principal.Scopes.Should().Equal("payment:write", "payment:read");
    }

    [Fact]
    public async Task Rejects_numeric_scope_claim()
    {
        string token = Sign("""{"sub":"candidate-client","consumer":"web","scope":1,"iat":1710000000,"exp":4102444800,"iss":"moduledev-course","aud":"moduledev-api"}""");
        JwtPrincipal principal = await _authenticator.AuthenticateAsync("Bearer " + token);
        principal.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_missing_bearer()
    {
        JwtPrincipal principal = await _authenticator.AuthenticateAsync(null);
        principal.IsAuthenticated.Should().BeFalse();
    }

    private static string Sign(string payloadJson)
    {
        string header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        string payload = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        string signingInput = header + "." + payload;
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(SigningKey));
        string signature = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
