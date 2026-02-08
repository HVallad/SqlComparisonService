using System.Text;
using System.Text.RegularExpressions;

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
        result = NormalizeTrailingCommaBeforePeriodForSystemTime(result);

        // Canonicalize whitespace immediately before parentheses so that
        // type declarations like "TIME (0)" and "TIME(0)" compare equal
        // and to reduce noise from stylistic formatting differences.
        result = NormalizeSpacesBeforeParentheses(result);

        // Treat DATETIME2 and DATETIME2(7) as equivalent, since SQL Server
        // uses a default precision of 7 when none is specified. This
        // ensures that database scripts which emit "DATETIME2" and file
        // scripts which use "DATETIME2 (7)" normalize to the same form.
        result = NormalizeDateTime2DefaultPrecision(result);

        // Treat FLOAT and FLOAT(53) as equivalent, since SQL Server uses
        // a default precision of 53 when none is specified. This ensures
        // that database scripts which emit "FLOAT" and file scripts which
        // use "FLOAT(53)" normalize to the same form.
        result = NormalizeFloatDefaultPrecision(result);

        // Treat DECIMAL(p) and DECIMAL(p, 0) as equivalent, since SQL Server
        // uses a default scale of 0 when none is specified. The same applies
        // to NUMERIC which is a synonym for DECIMAL.
        result = NormalizeDecimalDefaultScale(result);

        // Treat TIME and TIME(7) as equivalent, since SQL Server uses a
        // default precision of 7 when none is specified. This ensures that
        // database scripts which emit "TIME" and file scripts which use
        // "TIME(7)" normalize to the same form.
        result = NormalizeTimeDefaultPrecision(result);

        // Remove trailing commas before closing parenthesis in CREATE TABLE
        // statements. Some file scripts have a trailing comma after the last
        // column like "...[ExecutionDate] DATETIME NULL,\n)" which should be
        // normalized to "...[ExecutionDate] DATETIME NULL\n)".
        result = RemoveTrailingCommas(result);

        // Normalize newlines before closing parenthesis in CREATE TABLE
        // statements. Database scripts may have the closing paren on a new
        // line while file scripts have it on the same line as the last column.
        // This normalizes "...[Col] TYPE NULL\n)" to "...[Col] TYPE NULL)".
        result = NormalizeNewlinesBeforeClosingParen(result);

        // Canonicalize insignificant whitespace immediately after commas so
        // that constructs like "NUMERIC(18,3)" and "NUMERIC(18, 3)" or
        // "IDENTITY(1,1)" and "IDENTITY(1, 1)" normalize to the same form.
        // This is applied after other type-specific normalizations so that
        // we do not interfere with more targeted logic.
        result = NormalizeSpacesAfterCommas(result);

        // Strip a trailing semicolon at the end of the script so
        // that scripts which differ only by an optional terminal
        // semicolon compare equal while preserving inner semicolons.
        // This must run BEFORE NormalizeWithClauseOptions because
        // that method checks whether the WITH clause is at the end
        // of the script.
        result = TrimTrailingSemicolon(result);

        // Canonicalize WITH clause options to a consistent order so that
        // "WITH(DURABILITY = SCHEMA_ONLY, MEMORY_OPTIMIZED = ON)" and
        // "WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)" compare equal.
        result = NormalizeWithClauseOptions(result);

        // Normalize temporal column definitions by removing the HIDDEN keyword.
        // File scripts may have "GENERATED ALWAYS AS ROW START HIDDEN NOT NULL"
        // but the database generates "GENERATED ALWAYS AS ROW START NOT NULL".
        // We normalize to the form without HIDDEN for comparison.
        result = NormalizeTemporalColumnHidden(result);

        // Normalize CLR object definitions to extract only the EXTERNAL NAME clause.
        // File scripts have full CREATE statements: "CREATE PROCEDURE [tSQLt].[Proc] ... AS EXTERNAL NAME [assembly].[class].[method]"
        // Database returns only: "EXTERNAL NAME [assembly].[class].[method]"
        // We normalize to just the EXTERNAL NAME clause for comparison.
        result = NormalizeClrDefinition(result);

        // Normalize CREATE USER statements by stripping clauses after the
        // user name. Database generates "WITH DEFAULT_SCHEMA = [dbo]" but
        // file scripts may have "WITHOUT LOGIN" or "FOR LOGIN [name]".
        // We normalize to just "CREATE USER [name]" for comparison.
        result = NormalizeCreateUserSyntax(result);

        // Normalize CREATE ROLE statements by removing the AUTHORIZATION
        // clause. File scripts may have "AUTHORIZATION [dbo]" but the
        // database generates just "CREATE ROLE [name]".
        result = NormalizeCreateRoleSyntax(result);

        return result;
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
    /// Normalizes away insignificant whitespace immediately before opening
    /// parentheses when there is a non-whitespace character directly
    /// before the space. This turns "DATETIME2 (7)" into "DATETIME2(7)"
    /// and "TIME (0)" into "TIME(0)", while preserving indentation and
    /// spacing elsewhere.
    /// </summary>
    private static string NormalizeSpacesBeforeParentheses(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        return Regex.Replace(script, @"(?<=\S)\s+\(", "(", RegexOptions.Multiline);
    }

    /// <summary>
    /// Canonicalizes DATETIME2 declarations so that bare DATETIME2 and
    /// DATETIME2(7) (with any amount of whitespace) normalize to the same
    /// representation. This reflects SQL Server's default precision of 7
    /// for DATETIME2 and avoids spurious differences when one side omits
    /// the precision while the other spells it out as (7).
    ///
    /// Other precisions such as DATETIME2(3) remain distinct and continue
    /// to be treated as real changes.
    /// </summary>
    private static string NormalizeDateTime2DefaultPrecision(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Normalize DATETIME2 declarations so that bare DATETIME2 and
        // DATETIME2(7) (with any whitespace) both become DATETIME2(7),
        // while preserving other explicit precisions such as
        // DATETIME2(3).
        script = Regex.Replace(
            script,
            @"\bDATETIME2\b(?:\s*\(\s*(\d+)\s*\))?",
            match =>
            {
                var precisionGroup = match.Groups[1];
                var precisionText = precisionGroup.Success ? precisionGroup.Value : string.Empty;

                if (string.IsNullOrEmpty(precisionText) || precisionText == "7")
                {
                    return "DATETIME2(7)";
                }

                // For non-default precisions, keep the original text so
                // that real differences (e.g., DATETIME2(3) vs
                // DATETIME2(7)) are still detected.
                return match.Value;
            },
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes FLOAT declarations so that bare FLOAT and FLOAT(53) both
    /// become FLOAT(53), since SQL Server uses 53 as the default precision.
    /// This ensures that database scripts which emit "FLOAT" and file scripts
    /// which use "FLOAT(53)" normalize to the same form.
    ///
    /// Other precisions such as FLOAT(24) remain distinct and continue to be
    /// treated as real changes.
    /// </summary>
    private static string NormalizeFloatDefaultPrecision(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Normalize FLOAT declarations so that bare FLOAT and FLOAT(53)
        // (with any whitespace) both become FLOAT(53), while preserving
        // other explicit precisions such as FLOAT(24).
        script = Regex.Replace(
            script,
            @"\bFLOAT\b(?:\s*\(\s*(\d+)\s*\))?",
            match =>
            {
                var precisionGroup = match.Groups[1];
                var precisionText = precisionGroup.Success ? precisionGroup.Value : string.Empty;

                if (string.IsNullOrEmpty(precisionText) || precisionText == "53")
                {
                    return "FLOAT(53)";
                }

                // For non-default precisions, keep the original text so
                // that real differences (e.g., FLOAT(24) vs FLOAT(53))
                // are still detected.
                return match.Value;
            },
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes DECIMAL and NUMERIC type declarations so that bare
    /// DECIMAL(p) and DECIMAL(p, 0) both become DECIMAL(p, 0). SQL Server
    /// uses a default scale of 0 when none is specified, so these forms
    /// are semantically equivalent. This ensures that database scripts
    /// which emit "DECIMAL(19, 0)" and file scripts which use "DECIMAL(19)"
    /// normalize to the same form.
    ///
    /// Explicit scales other than 0 remain distinct.
    /// </summary>
    private static string NormalizeDecimalDefaultScale(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Match DECIMAL(p) or DECIMAL(p, 0) or DECIMAL(p, s) where s != 0
        // and NUMERIC(p) or NUMERIC(p, 0) or NUMERIC(p, s) where s != 0
        // We normalize DECIMAL(p) to DECIMAL(p, 0) and NUMERIC(p) to NUMERIC(p, 0)
        script = Regex.Replace(
            script,
            @"\b(DECIMAL|NUMERIC)\s*\(\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)",
            match =>
            {
                var typeName = match.Groups[1].Value.ToUpperInvariant();
                var precision = match.Groups[2].Value;
                var scaleGroup = match.Groups[3];
                var scale = scaleGroup.Success ? scaleGroup.Value : "0";

                // Normalize to the explicit form with scale
                return $"{typeName}({precision}, {scale})";
            },
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes TIME type declarations so that bare TIME and TIME(7) both
    /// become TIME(7). SQL Server uses a default precision of 7 when none is
    /// specified, so "TIME" and "TIME(7)" are semantically identical. This
    /// ensures that database scripts which emit "TIME" and file scripts
    /// which use "TIME(7)" normalize to the same form.
    ///
    /// Other precisions such as TIME(3) remain distinct and continue to be
    /// treated as real changes.
    /// </summary>
    private static string NormalizeTimeDefaultPrecision(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Normalize TIME declarations so that bare TIME and TIME(7)
        // (with any whitespace) both become TIME(7), while preserving
        // other explicit precisions such as TIME(3).
        script = Regex.Replace(
            script,
            @"\bTIME\b(?:\s*\(\s*(\d+)\s*\))?",
            match =>
            {
                var precisionGroup = match.Groups[1];
                var precisionText = precisionGroup.Success ? precisionGroup.Value : string.Empty;

                if (string.IsNullOrEmpty(precisionText) || precisionText == "7")
                {
                    return "TIME(7)";
                }

                // For non-default precisions, keep the original text so
                // that real differences (e.g., TIME(3) vs TIME(7))
                // are still detected.
                return match.Value;
            },
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Removes trailing commas before closing parenthesis in CREATE TABLE
    /// statements. Some file scripts have a trailing comma after the last
    /// column definition, which is syntactically invalid but may be present
    /// due to manual editing. This normalization removes such trailing commas
    /// so that scripts compare equal regardless of trailing comma presence.
    /// </summary>
    private static string RemoveTrailingCommas(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Remove trailing commas before closing parenthesis, allowing for
        // whitespace and newlines between the comma and the closing paren.
        // This handles cases like "...[ExecutionDate] DATETIME NULL,\n)".
        script = Regex.Replace(
            script,
            @",\s*\)",
            ")",
            RegexOptions.Multiline);

        return script;
    }

    /// <summary>
    /// Normalizes newlines before closing parenthesis in CREATE TABLE statements.
    /// Database scripts may have the closing paren on a new line while file
    /// scripts have it on the same line as the last column. This normalizes
    /// "...[Col] TYPE NULL\n)" to "...[Col] TYPE NULL)".
    /// </summary>
    private static string NormalizeNewlinesBeforeClosingParen(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Remove newlines (and any surrounding whitespace) immediately before
        // a closing parenthesis. This handles cases where the database script
        // has the closing paren on a new line but the file script has it on
        // the same line as the last column.
        script = Regex.Replace(
            script,
            @"\s*\n\s*\)",
            ")",
            RegexOptions.Multiline);

        return script;
    }

    /// <summary>
    /// Normalizes CREATE USER statements by stripping clauses after the user name.
    /// Database generates "CREATE USER [name] WITH DEFAULT_SCHEMA = [dbo]" but
    /// file scripts may have "CREATE USER [name] WITHOUT LOGIN" or
    /// "CREATE USER [name] FOR LOGIN [loginname]". We normalize to just
    /// "CREATE USER [name]" for comparison purposes.
    /// </summary>
    private static string NormalizeCreateUserSyntax(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Match CREATE USER [name] followed by optional clauses and strip them.
        // This handles WITH DEFAULT_SCHEMA, WITHOUT LOGIN, FOR LOGIN patterns.
        script = Regex.Replace(
            script,
            @"(CREATE\s+USER\s+\[[^\]]+\])(\s+(WITH\s+DEFAULT_SCHEMA\s*=\s*\[[^\]]+\]|WITHOUT\s+LOGIN|FOR\s+LOGIN\s+\[[^\]]+\]))*",
            "$1",
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes CREATE ROLE statements by removing the AUTHORIZATION clause.
    /// File scripts may have "CREATE ROLE [name] AUTHORIZATION [dbo]" but the
    /// database generates just "CREATE ROLE [name]".
    /// </summary>
    private static string NormalizeCreateRoleSyntax(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Remove AUTHORIZATION [owner] clause from CREATE ROLE statements.
        script = Regex.Replace(
            script,
            @"(CREATE\s+ROLE\s+\[[^\]]+\])\s+AUTHORIZATION\s+\[[^\]]+\]",
            "$1",
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes temporal column definitions by removing the HIDDEN keyword.
    /// File scripts may have "GENERATED ALWAYS AS ROW START HIDDEN NOT NULL"
    /// but the database generates "GENERATED ALWAYS AS ROW START NOT NULL".
    /// The HIDDEN keyword is optional and doesn't affect the column behavior,
    /// so we normalize to the form without HIDDEN for comparison.
    /// </summary>
    private static string NormalizeTemporalColumnHidden(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Remove HIDDEN keyword from GENERATED ALWAYS AS ROW START/END columns.
        // Pattern matches: GENERATED ALWAYS AS ROW START HIDDEN or GENERATED ALWAYS AS ROW END HIDDEN
        script = Regex.Replace(
            script,
            @"(GENERATED\s+ALWAYS\s+AS\s+ROW\s+(?:START|END))\s+HIDDEN",
            "$1",
            RegexOptions.IgnoreCase);

        return script;
    }

    /// <summary>
    /// Normalizes CLR object definitions by extracting only the EXTERNAL NAME clause.
    /// File scripts have full CREATE statements like:
    /// "CREATE PROCEDURE [tSQLt].[Proc] @param NVARCHAR(MAX) AS EXTERNAL NAME [assembly].[class].[method]"
    /// Database returns only: "EXTERNAL NAME [assembly].[class].[method]"
    /// We normalize to just the EXTERNAL NAME clause for comparison.
    /// </summary>
    private static string NormalizeClrDefinition(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Check if script contains "EXTERNAL NAME" clause (CLR object indicator)
        // Use case-insensitive match with flexible whitespace
        var externalNameMatch = Regex.Match(
            script,
            @"EXTERNAL\s+NAME\s+\[.+?\]\.\[.+?\]\.\[.*?\]",
            RegexOptions.IgnoreCase);

        if (externalNameMatch.Success)
        {
            // Return only the EXTERNAL NAME clause for comparison
            return externalNameMatch.Value;
        }

        // Not a CLR object, return unchanged
        return script;
    }

    /// <summary>
    /// Normalizes away insignificant whitespace immediately after commas in
    /// SQL tokens, while avoiding changes inside string literals and
    /// bracketed identifiers. This turns "NUMERIC(18,3)" and
    /// "NUMERIC(18, 3)" into the same representation and likewise for
    /// IDENTITY seeds/increments such as "IDENTITY(1,1)" vs
    /// "IDENTITY(1, 1)".
    /// </summary>
    private static string NormalizeSpacesAfterCommas(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        var builder = new StringBuilder(script.Length);
        var inSingleQuote = false;
        var inBracket = false;

        for (var i = 0; i < script.Length; i++)
        {
            var ch = script[i];

            // Track simple single-quoted string literals so that we do not
            // alter commas/spaces inside them. Handle escaped '' sequences
            // by keeping them inside the string.
            if (!inBracket && ch == '\'')
            {
                if (inSingleQuote)
                {
                    if (i + 1 < script.Length && script[i + 1] == '\'')
                    {
                        builder.Append("''");
                        i++;
                        continue;
                    }

                    inSingleQuote = false;
                    builder.Append(ch);
                    continue;
                }

                inSingleQuote = true;
                builder.Append(ch);
                continue;
            }

            // Track bracketed identifiers like [Name, With, Commas] so that
            // we likewise avoid altering their contents.
            if (!inSingleQuote)
            {
                if (ch == '[')
                {
                    inBracket = true;
                    builder.Append(ch);
                    continue;
                }

                if (ch == ']' && inBracket)
                {
                    inBracket = false;
                    builder.Append(ch);
                    continue;
                }
            }

            if (!inSingleQuote && !inBracket && ch == ',')
            {
                builder.Append(',');
                var j = i + 1;

                // Skip any spaces or tabs immediately following the comma,
                // but leave newlines and other whitespace intact.
                while (j < script.Length && (script[j] == ' ' || script[j] == '\t'))
                {
                    j++;
                }

                // If the next non-space character is not a newline, carriage
                // return, or closing parenthesis, insert a single space to
                // canonicalize the separation.
                if (j < script.Length && script[j] != '\n' && script[j] != '\r' && script[j] != ')')
                {
                    builder.Append(' ');
                }

                i = j - 1;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes a single optional semicolon at the very end of the script
    /// while leaving all inner semicolons intact. This allows scripts that
    /// differ only by a trailing semicolon on the final statement to
    /// compare equal.
    /// </summary>
    private static string TrimTrailingSemicolon(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        var end = script.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(script[end]))
        {
            end--;
        }

        if (end >= 0 && script[end] == ';')
        {
            return script[..end];
        }

        return script;
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

    /// <summary>
    /// Normalizes WITH clause options in table DDL to a canonical order.
    /// This ensures that "WITH(DURABILITY = SCHEMA_ONLY, MEMORY_OPTIMIZED = ON)"
    /// and "WITH (MEMORY_OPTIMIZED = ON, DURABILITY = SCHEMA_ONLY)" compare equal.
    ///
    /// The options are sorted alphabetically by their key (DURABILITY, MEMORY_OPTIMIZED,
    /// SYSTEM_VERSIONING, etc.) to produce a consistent canonical form.
    /// </summary>
    private static string NormalizeWithClauseOptions(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return script;
        }

        // Find WITH clause at the end of the script. We can't use a simple regex
        // because the WITH clause can contain nested parentheses like:
        // WITH(SYSTEM_VERSIONING = ON(HISTORY_TABLE = [schema].[table], DATA_CONSISTENCY_CHECK=ON))
        // So we need to find the matching closing parenthesis manually.

        // Find "WITH" followed by optional whitespace and opening paren
        var withMatch = Regex.Match(script, @"WITH\s*\(", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        if (!withMatch.Success)
        {
            return script;
        }

        var withStart = withMatch.Index;
        var openParenIndex = withMatch.Index + withMatch.Length - 1; // Position of '('

        // Find the matching closing parenthesis
        var parenDepth = 1;
        var closeParenIndex = -1;
        for (var i = openParenIndex + 1; i < script.Length; i++)
        {
            if (script[i] == '(')
            {
                parenDepth++;
            }
            else if (script[i] == ')')
            {
                parenDepth--;
                if (parenDepth == 0)
                {
                    closeParenIndex = i;
                    break;
                }
            }
        }

        if (closeParenIndex == -1)
        {
            // No matching closing paren found
            return script;
        }

        // Check that this WITH clause is at the end of the script (only whitespace after)
        var afterWith = script.Substring(closeParenIndex + 1).Trim();
        if (!string.IsNullOrEmpty(afterWith))
        {
            // There's content after the WITH clause, so this isn't the table's WITH clause
            return script;
        }

        // Extract the content inside the WITH parentheses
        var optionsContent = script.Substring(openParenIndex + 1, closeParenIndex - openParenIndex - 1);

        // Split options by comma, but be careful with nested parentheses like
        // "SYSTEM_VERSIONING = ON (HISTORY_TABLE = [schema].[table])"
        var options = SplitWithClauseOptions(optionsContent);

        // Normalize each option (trim whitespace) and apply option-specific
        // normalization (e.g., removing default values from SYSTEM_VERSIONING)
        var normalizedOptions = options
            .Select(opt => opt.Trim())
            .Where(opt => !string.IsNullOrEmpty(opt))
            .Select(NormalizeWithClauseOption)
            .OrderBy(opt => opt, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedOptions.Count == 0)
        {
            return script;
        }

        // Rebuild the script with the normalized WITH clause
        var prefix = script.Substring(0, withStart);
        return $"{prefix}WITH ({string.Join(", ", normalizedOptions)})";
    }

    /// <summary>
    /// Splits WITH clause options by comma, respecting nested parentheses.
    /// For example: "MEMORY_OPTIMIZED = ON, SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[Hist])"
    /// should split into two options, not three.
    /// </summary>
    private static List<string> SplitWithClauseOptions(string content)
    {
        var options = new List<string>();
        var current = new StringBuilder();
        var parenDepth = 0;

        foreach (var ch in content)
        {
            if (ch == '(')
            {
                parenDepth++;
                current.Append(ch);
            }
            else if (ch == ')')
            {
                parenDepth--;
                current.Append(ch);
            }
            else if (ch == ',' && parenDepth == 0)
            {
                // This comma is a separator between options
                if (current.Length > 0)
                {
                    options.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        // Don't forget the last option
        if (current.Length > 0)
        {
            options.Add(current.ToString());
        }

        return options;
    }

    /// <summary>
    /// Normalizes a single WITH clause option. This handles option-specific
    /// normalization such as canonicalizing whitespace around = signs.
    /// </summary>
    private static string NormalizeWithClauseOption(string option)
    {
        if (string.IsNullOrEmpty(option))
        {
            return option;
        }

        // Normalize whitespace around = signs so that "KEY=VALUE" and "KEY = VALUE"
        // both become "KEY = VALUE" for consistent comparison.
        var normalized = Regex.Replace(option, @"\s*=\s*", " = ");
        return normalized;
    }

    /// <summary>
    /// Normalizes an index script for comparison. This applies <see cref="NormalizeForComparison"/>
    /// and then collapses all newlines (with surrounding whitespace) into single spaces.
    ///
    /// Index scripts are single-statement DDL where newlines are purely stylistic formatting,
    /// so "CREATE INDEX [X] ON [T]([A])" and "CREATE INDEX [X]\n    ON [T]([A])" should compare equal.
    /// </summary>
    public static string NormalizeIndexForComparison(string? script)
    {
        var normalized = NormalizeForComparison(script);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        // Replace any sequence of whitespace (including newlines) with a single space.
        // This collapses multi-line index scripts into a single canonical line.
        var result = new StringBuilder(normalized.Length);
        var lastWasSpace = false;

        foreach (var ch in normalized)
        {
            if (ch == '\n' || ch == '\r' || ch == ' ' || ch == '\t')
            {
                if (!lastWasSpace)
                {
                    result.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                result.Append(ch);
                lastWasSpace = false;
            }
        }

        return result.ToString().Trim();
    }
}

