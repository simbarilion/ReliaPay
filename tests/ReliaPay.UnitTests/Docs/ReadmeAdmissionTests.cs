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
        readme.Should().Contain("docker compose up -d --build");
        readme.Should().Contain("./check.sh");
    }

    [Fact]
    public void Readme_declares_week_two_solution_headings()
    {
        string readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "README.md"));
        readme.Should().Contain("### Архитектура");
        readme.Should().Contain("### Запуск");
        readme.Should().Contain("### Workflow-карты");
        readme.Should().Contain("### Worker");
        readme.Should().Contain("### Python-периметр");
        readme.Should().Contain("### Provider");
        readme.Should().Contain("### Проверка");
        readme.Should().Contain("### Диагностика");
        readme.Should().Contain("### Ограничения");
    }
}
