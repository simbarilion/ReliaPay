using System.Security.Cryptography;
using System.Text;

namespace ReliaPay.TestSupport;

public static class JwtTestToken
{
    public static string MintCandidate()
    {
        return Mint(
            "candidate-client",
            "web",
            "payment:write payment:read workflow:read");
    }

    public static string MintDenied()
    {
        return Mint("denied-client", "test", "");
    }

    public static string Mint(string sub, string consumer, string scope)
    {
        string issuer = Environment.GetEnvironmentVariable("COURSE_JWT_ISSUER") ?? "moduledev-course";
        string audience = Environment.GetEnvironmentVariable("COURSE_JWT_AUDIENCE") ?? "moduledev-api";
        string signingKey = Environment.GetEnvironmentVariable("COURSE_JWT_SIGNING_KEY")
            ?? throw new InvalidOperationException("COURSE_JWT_SIGNING_KEY is required for gateway tests.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string payload =
            $"{{\"sub\":\"{sub}\",\"consumer\":\"{consumer}\",\"scope\":\"{scope}\",\"iat\":{now},\"exp\":{now + 3600},\"iss\":\"{issuer}\",\"aud\":\"{audience}\"}}";
        string header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        string body = Base64Url(Encoding.UTF8.GetBytes(payload));
        string signingInput = header + "." + body;
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(signingKey));
        string signature = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
