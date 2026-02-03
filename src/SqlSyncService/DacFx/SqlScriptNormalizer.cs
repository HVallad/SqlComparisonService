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

        // Trim leading blank/whitespace-only lines
        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        // Trim trailing blank/whitespace-only lines
        var end = lines.Length - 1;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (end < start)
        {
            return string.Empty;
        }

        // If the last non-empty line is a GO batch, remove it and any blank lines before it
        if (IsGoLine(lines[end]))
        {
            end--; // drop GO line
            while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
            {
                end--;
            }
        }

        if (end < start)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            if (i > start)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes a script for content comparison. This builds on <see cref="Normalize"/>
    /// by additionally canonicalizing intra-line whitespace so that semantically
    /// equivalent scripts with different spacing (e.g. multiple spaces vs a
    /// single space between tokens) compare equal.
    ///
    /// The normalization rules are:
    /// - Line endings and trailing blank lines are handled by <see cref="Normalize"/>.
    /// - Trailing whitespace on each line is trimmed.
    /// - Leading indentation is preserved exactly.
    /// - After the first non-whitespace character on a line, any run of spaces
    ///   or tabs is collapsed to a single space character.
    /// </summary>
    public static string NormalizeForComparison(string? script)
    {
        var normalized = Normalize(script);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Trim trailing whitespace but keep leading indentation as-is.
            line = line.TrimEnd();

            var builder = new StringBuilder(line.Length);
            var seenNonWhitespace = false;
            var lastWasSpace = false;
            for (var j = 0; j < line.Length; j++)
            {
                var ch = line[j];
                if (!seenNonWhitespace)
                {
                    if (ch == ' ' || ch == '\t')
                    {
                        // Preserve leading indentation exactly.
                        builder.Append(ch);
                        continue;
                    }

                    seenNonWhitespace = true;
                    lastWasSpace = false;
                    builder.Append(ch);
                    continue;
                }

                if (ch == ' ' || ch == '\t')
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    builder.Append(ch);
                    lastWasSpace = false;
                }
            }

            lines[i] = builder.ToString();
        }

        var result = string.Join("\n", lines);
        // Apply the same temporal trailing-comma normalization used in
        // StripInlineConstraints, so that callers which only use
        // NormalizeForComparison (or which pass through additional
        // transformations) still get consistent handling of
        // "final column + PERIOD FOR SYSTEM_TIME" patterns.
        return NormalizeTrailingCommaBeforePeriodForSystemTime(result);
    }

    /// <summary>
    /// For multi-batch scripts, returns only the first batch (everything before the
    /// first GO line), trimming any blank lines immediately before that GO.
    ///
    /// Assumes <paramref name="script"/> has already been normalized with <see cref="Normalize"/>
    /// (i.e. uses '\n' newlines and has trailing whitespace/GO removed).
    /// </summary>
    public static string TruncateAfterFirstGo(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return string.Empty;
        }

        var batches = SplitBatches(script!);
        return batches.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// For table scripts, removes inline constraints from the CREATE TABLE definition
    /// so that only column definitions (including nullability) remain for comparison.
    ///
    /// This operates in two passes:
    ///  1. Within the column list, strips column-level inline constraint/default
    ///     segments such as "CONSTRAINT [DF_...] DEFAULT (...)" while preserving
    ///     the column definition and trailing NULL/NOT NULL.
    ///  2. Removes table-level constraint lines (those that start with CONSTRAINT,
    ///     PRIMARY KEY, FOREIGN KEY, etc.) as a whole block.
    ///
    /// Assumes <paramref name="script"/> has already been normalized with
    /// <see cref="Normalize"/> (i.e. uses '\n' newlines and has trailing
    /// whitespace/GO removed).
    /// </summary>
    public static string StripInlineConstraints(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return string.Empty;
        }

        var lines = script!.Split('\n');

        // Locate the CREATE TABLE line and the opening parenthesis.
        var createTableLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf("create table", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                createTableLine = i;
                break;
            }
        }

        if (createTableLine == -1)
        {
            // Not a table script; return as-is.
            return script!;
        }

        var openLine = -1;
        for (var i = createTableLine; i < lines.Length; i++)
        {
            if (lines[i].Contains('('))
            {
                openLine = i;
                break;
            }
        }

        if (openLine == -1)
        {
            return script!;
        }

        // Find the closing parenthesis for the column/constraint list.
        var closeLine = -1;
        for (var i = lines.Length - 1; i >= openLine; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith(")", StringComparison.Ordinal) || trimmed.EndsWith(");", StringComparison.Ordinal))
            {
                closeLine = i;
                break;
            }
        }

        if (closeLine == -1 || closeLine <= openLine + 1)
        {
            return script!;
        }

        // First pass: strip column-level inline constraints/defaults on each
        // column line while preserving the column definition and nullability.
        for (var i = openLine + 1; i < closeLine; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            // Skip standalone constraint lines; these are handled in the
            // second pass as table-level constraints.
            if (IsConstraintLine(trimmed))
            {
                continue;
            }

            lines[i] = StripColumnLevelConstraint(line);
        }

        // Second pass: identify the first table-level constraint line and track
        // the last column line before constraints begin. We also detect whether
        // there are any non-constraint lines (such as PERIOD FOR SYSTEM_TIME)
        // between the first constraint and the closing parenthesis. If such
        // lines exist, we should keep the trailing comma on the last column to
        // match the shape of the DacFx script.
        var firstConstraintLine = -1;
        var lastColumnLine = -1;
        var hasNonConstraintBetweenConstraintsAndClose = false;
        for (var i = openLine + 1; i < closeLine; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (IsConstraintLine(line))
            {
                if (firstConstraintLine == -1)
                {
                    firstConstraintLine = i;
                }

                continue;
            }

            var isPeriodLine = IsPeriodForSystemTimeLine(line);

            if (firstConstraintLine == -1)
            {
                // Before any table-level constraints: track the last actual
                // column definition line. Temporal PERIOD FOR SYSTEM_TIME
                // lines are not treated as columns so that we do not
                // accidentally strip the ", [ValidTo])" portion when
                // normalizing trailing commas.
                if (!isPeriodLine)
                {
                    lastColumnLine = i;
                }
            }
            else
            {
                // After the first table-level constraint, any non-constraint
                // line (including PERIOD FOR SYSTEM_TIME) counts as a
                // non-constraint between the constraints and the closing
                // parenthesis.
                hasNonConstraintBetweenConstraintsAndClose = true;
            }
        }

        // Remove a trailing comma from the last column definition when we
        // are effectively dropping all following items for comparison. This
        // applies both when table-level constraints exist (and are removed)
        // and when there are no constraints at all, so that scripts which
        // differ only by a trailing comma on the final column compare equal.
        //
        // When there *are* table-level constraints, we still respect
        // non-constraint lines (such as PERIOD FOR SYSTEM_TIME) that appear
        // after the constraints by keeping the comma to match the shape of
        // the DacFx-produced script.
        if (lastColumnLine >= 0 && (firstConstraintLine == -1 || !hasNonConstraintBetweenConstraintsAndClose))
        {
            var line = lines[lastColumnLine];
            var trimmed = line.TrimEnd();

            // Only remove a trailing comma when it is actually the last
            // non-whitespace character on the line. This avoids chopping
            // commas that belong to type parameter lists such as
            // NUMERIC(18, 2).
            if (trimmed.EndsWith(",", StringComparison.Ordinal))
            {
                var commaIndex = line.LastIndexOf(',');
                if (commaIndex >= 0)
                {
                    lines[lastColumnLine] = line.Substring(0, commaIndex);
                }
            }
        }

        var builder = new StringBuilder(script.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            // Between the first table-level constraint and the closing
            // parenthesis, drop only lines that are themselves constraint
            // lines. This preserves non-constraint lines such as temporal
            // PERIOD FOR SYSTEM_TIME clauses that appear after the
            // constraints.
            if (firstConstraintLine != -1 && i >= firstConstraintLine && i < closeLine && IsConstraintLine(lines[i]))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i]);
        }

        // As a final normalization step for temporal tables, ensure that a
        // trailing comma on the last column immediately before a
        // PERIOD FOR SYSTEM_TIME clause is removed. This handles cases
        // where file-side scripts include a comma after the final column
        // while the DacFx-produced database script does not, so that such
        // scripts compare equal.
        var result = builder.ToString();
        return NormalizeTrailingCommaBeforePeriodForSystemTime(result);
    }

    /// <summary>
    /// Splits a normalized script into batches separated by GO lines. The GO
    /// tokens themselves are not included in the returned batches.
    /// </summary>
    public static IEnumerable<string> SplitBatches(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            yield break;
        }

        var lines = script.Split('\n');
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            if (IsGoLine(line))
            {
                // End of current batch.
                foreach (var b in YieldBatchesFromBuilder())
                {
                    yield return b;
                }
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        foreach (var b in YieldBatchesFromBuilder())
        {
            yield return b;
        }

        IEnumerable<string> YieldBatchesFromBuilder()
        {
            if (builder.Length == 0)
            {
                yield break;
            }

            var batch = builder.ToString().TrimEnd('\n');
            if (batch.Length > 0)
            {
                yield return batch;
            }

            builder.Clear();
        }
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

    private static bool IsConstraintLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsPeriodForSystemTimeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("PERIOD FOR SYSTEM_TIME", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTrailingCommaBeforePeriodForSystemTime(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        var lines = script.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsPeriodForSystemTimeLine(lines[i]))
            {
                continue;
            }

            // Walk backwards to find the preceding non-empty line (typically
            // the last column definition) and remove any trailing comma so
            // that the column and PERIOD FOR SYSTEM_TIME clause match the
            // shape of the database script.
            for (var j = i - 1; j >= 0; j--)
            {
                if (string.IsNullOrWhiteSpace(lines[j]))
                {
                    continue;
                }

                var line = lines[j];
                var commaIndex = line.LastIndexOf(',');
                if (commaIndex >= 0)
                {
                    lines[j] = line.Substring(0, commaIndex);
                }

                break;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Strips column-level inline constraint/default segments from a single
    /// column definition line. This is a heuristic intended to handle common
    /// patterns such as temporal table default constraints, e.g.:
    ///
    ///   [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START
    ///       CONSTRAINT [DF_ValidFrom] DEFAULT (sysutcdatetime()) NOT NULL,
    ///
    /// and turns it into:
    ///
    ///   [ValidFrom] DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ///
    /// preserving the column definition and nullability while removing the
    /// named default constraint.
    /// </summary>
    private static string StripColumnLevelConstraint(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        // Look for a CONSTRAINT token that is not at the very start of the
        // line (to avoid touching standalone constraint lines).
        var constraintIndex = line.IndexOf("CONSTRAINT", StringComparison.OrdinalIgnoreCase);
        if (constraintIndex <= 0)
        {
            return line;
        }

        var prefix = line.Substring(0, constraintIndex);

        // Try to preserve explicit NULL/NOT NULL that appears after the
        // default expression. We search from the end so that we prefer the
        // outer nullability modifier rather than anything inside the DEFAULT
        // expression itself.
        var notNullIndex = line.LastIndexOf("NOT NULL", StringComparison.OrdinalIgnoreCase);
        var nullIndex = line.LastIndexOf(" NULL", StringComparison.OrdinalIgnoreCase);
        var nullabilityIndex = -1;
        if (notNullIndex >= 0)
        {
            nullabilityIndex = notNullIndex;
        }
        else if (nullIndex >= 0)
        {
            nullabilityIndex = nullIndex;
        }

        string tail = string.Empty;
        if (nullabilityIndex > constraintIndex)
        {
            tail = line.Substring(nullabilityIndex);
        }

        var prefixTrimmed = prefix.TrimEnd();
        if (tail.Length == 0)
        {
            return prefixTrimmed;
        }

        // Ensure we don't accidentally join tokens without whitespace.
        if (prefixTrimmed.Length > 0 && !char.IsWhiteSpace(prefixTrimmed[^1]) && !char.IsWhiteSpace(tail[0]))
        {
            return prefixTrimmed + " " + tail.TrimStart();
        }

        return prefixTrimmed + tail;
    }
}

