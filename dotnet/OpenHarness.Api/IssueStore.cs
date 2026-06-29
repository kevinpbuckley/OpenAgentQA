using System.Text.Json;

namespace OpenHarness.Api;

/// <summary>
/// File-backed issue tracker (one JSON file per issue under <c>.harness/issues</c>),
/// ported from the TypeScript tracker. Issues are created when a test errors and can be
/// listed/shown/resolved from the CLI.
/// </summary>
internal static class IssueStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static Issue Create(string baseDir, string test, string severity, string summary, string trace)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(baseDir);
            var sequence = LoadSequence(baseDir) + 1;
            var id = $"ISSUE-{sequence:D3}";
            var issue = new Issue(id, test, "open", severity, summary, trace, DateTimeOffset.UtcNow.ToString("O"), null);
            File.WriteAllText(Path.Combine(baseDir, $"{id}.json"), JsonSerializer.Serialize(issue, Indented));
            File.WriteAllText(Path.Combine(baseDir, ".sequence"), sequence.ToString());
            return issue;
        }
    }

    public static IReadOnlyList<Issue> List(string baseDir)
    {
        if (!Directory.Exists(baseDir)) return [];
        return Directory.EnumerateFiles(baseDir, "ISSUE-*.json")
            .Select(TryRead)
            .Where(issue => issue is not null)
            .Cast<Issue>()
            .OrderByDescending(issue => issue.Created, StringComparer.Ordinal)
            .ToList();
    }

    public static Issue? Get(string baseDir, string id)
    {
        var path = Path.Combine(baseDir, $"{id}.json");
        return File.Exists(path) ? TryRead(path) : null;
    }

    public static Issue? Resolve(string baseDir, string id)
    {
        lock (Gate)
        {
            var issue = Get(baseDir, id);
            if (issue is null) return null;
            var resolved = issue with { Status = "resolved", Resolved = DateTimeOffset.UtcNow.ToString("O") };
            File.WriteAllText(Path.Combine(baseDir, $"{id}.json"), JsonSerializer.Serialize(resolved, Indented));
            return resolved;
        }
    }

    public static IReadOnlyList<Issue> OpenIssues(string baseDir) =>
        List(baseDir).Where(issue => issue.Status == "open").ToList();

    public static bool HasOpenForTest(string baseDir, string test) =>
        List(baseDir).Any(issue => issue.Test == test && issue.Status == "open");

    private static int LoadSequence(string baseDir)
    {
        var path = Path.Combine(baseDir, ".sequence");
        return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;
    }

    private static Issue? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<Issue>(File.ReadAllText(path)); }
        catch { return null; }
    }
}
