using System.Globalization;
using System.Text;

namespace ReliaPay.Api.Metrics;

/// <summary>
/// Сборка OpenMetrics 1.0 document с завершающим # EOF.
/// </summary>
internal static class OpenMetricsDocument
{
    internal const string ContentType = "application/openmetrics-text; version=1.0.0; charset=utf-8";

    internal static byte[] Build(IReadOnlyList<(string Family, string Type, string SampleName, double Value)> samples)
    {
        StringBuilder builder = new();
        string? lastFamily = null;
        foreach ((string family, string type, string sampleName, double value) in samples)
        {
            if (!string.Equals(lastFamily, family, StringComparison.Ordinal))
            {
                builder.Append("# TYPE ").Append(family).Append(' ').Append(type).Append('\n');
                lastFamily = family;
            }

            builder.Append(sampleName)
                .Append(' ')
                .Append(value.ToString("0.######", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        builder.Append("# EOF\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    internal static byte[] Empty()
    {
        return Encoding.UTF8.GetBytes("# EOF\n");
    }
}
