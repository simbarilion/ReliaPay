using FluentAssertions;
using ReliaPay.Catalog;
using ReliaPay.Json;

namespace ReliaPay.UnitTests.Catalog;

public sealed class BundledManifestSchemaTests
{
    [Fact]
    public void Bundled_action_manifests_match_canonical_course_schema()
    {
        ActionManifestReader reader = new(new JsonDocumentValidator());
        string directory = FindDirectory("db", "manifests");
        string[] files = Directory.GetFiles(directory, "*.json");
        files.Should().NotBeEmpty();

        foreach (string file in files)
        {
            ActionManifestReadResult result = reader.Read(File.ReadAllText(file));
            result.IsSuccess.Should().BeTrue(because: Path.GetFileName(file));
            result.ErrorCode.Should().BeNull();
        }
    }

    private static string FindDirectory(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Directory {string.Join('/', parts)} was not found.");
    }
}
