using FluentAssertions;

namespace ReliaPay.UnitTests.Docs;

public sealed class ReadmeAdmissionTests
{
    [Fact]
    public void Readme_contains_literal_localhost_gateway_url()
    {
        string readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "README.md"));
        readme.Should().Contain("http://localhost:8080");
        readme.Should().Contain("http://localhost:8080/health/ready");
    }
}
