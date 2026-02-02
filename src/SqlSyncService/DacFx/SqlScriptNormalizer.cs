using System.Text;

namespace SqlSyncService.DacFx;

/// <summary>
/// Normalizes T-SQL scripts for stable comparison.
/// - Normalizes line endings
/// - Trims trailing blank lines
/// - Removes a trailing GO batch (and surrounding blank lines)
/// </summary>
internal static class SqlScriptNormalizer
{
    public static string Normalize(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return string.Empty;
        }

        // Normalize line endings to '\n'
        var normalized = script
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        var lines = normalized.Split('\n');

        // Trim trailing blank/whitespace-only lines
        var end = lines.Length - 1;
        while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < 0)
        {
            return string.Empty;
        }

        // If the last non-empty line is a GO batch, remove it and any blank lines before it
        if (IsGoLine(lines[end]))
        {
            end--; // drop GO line
            while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
            {
                end--;
            }
        }

        if (end < 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i <= end; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    private static bool IsGoLine(string? line)
    {
        if (line is null)
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // Allow optional trailing semicolon: "GO" or "GO;"
        if (trimmed.EndsWith(";", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase);
    }
}

