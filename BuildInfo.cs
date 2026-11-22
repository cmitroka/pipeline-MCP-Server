using System.Globalization;
using System.Reflection;

namespace JobPipelineMcp;

internal static class BuildInfo
{
    public static string BuiltAtUtc { get; } = ReadBuildTimestamp();

    private static string ReadBuildTimestamp()
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildTimestamp")
            ?.Value;

        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        // Normalize whatever MSBuild / Docker wrote into a clear UTC string.
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }

        return value.Trim();
    }
}
