namespace VectorNNTP.NNTPD.Tests.Fixtures;

/// <summary>Loads the operator DDL at <c>docs/postfilter.sql</c>.</summary>
internal static class PostFilterSchemaScript
{
    public static string FindPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "postfilter.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("docs/postfilter.sql was not found.");
    }

    public static IReadOnlyList<string> ReadStatements()
    {
        var lines = File.ReadAllLines(FindPath());
        var statements = new List<string>();
        var buffer = new List<string>();
        var inTrigger = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("-- BEGIN TRIGGER", StringComparison.Ordinal))
            {
                Flush(buffer, statements);
                inTrigger = true;
                continue;
            }

            if (line.StartsWith("-- END TRIGGER", StringComparison.Ordinal))
            {
                Flush(buffer, statements);
                inTrigger = false;
                continue;
            }

            if (!inTrigger && line.StartsWith("-- VECTORNNTP_STMT", StringComparison.Ordinal))
            {
                Flush(buffer, statements);
                continue;
            }

            if (line.StartsWith("--", StringComparison.Ordinal) && !inTrigger)
            {
                continue;
            }

            buffer.Add(line);
        }

        Flush(buffer, statements);
        return statements;
    }

    private static void Flush(List<string> buffer, List<string> statements)
    {
        var text = string.Join('\n', buffer).Trim();
        buffer.Clear();
        if (text.Length == 0)
        {
            return;
        }

        statements.Add(text.TrimEnd(';').Trim());
    }
}
