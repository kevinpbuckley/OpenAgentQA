using System.Text.Json;

namespace OpenHarness.Api;

/// <summary>
/// Deterministic run-to-run comparison (the "scoreboard"). Reads two runs' <c>report.json</c> and diffs
/// their metrics + per-test status, so you — or the AI that fixes the skills / system prompt / MCP tools —
/// can tell whether a change actually improved things, without an LLM re-reading two full transcripts.
/// Pure counts and deltas; no interpretation.
/// </summary>
internal static class RunComparer
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static RunComparison? Compare(string workspace, string idBefore, string idAfter)
    {
        var before = ReadReport(workspace, idBefore);
        var after = ReadReport(workspace, idAfter);
        if (before is null || after is null) return null;
        return new RunComparison(idBefore, idAfter, Metrics(before), Metrics(after), Changes(before, after));
    }

    private static RunReport? ReadReport(string workspace, string id)
    {
        var runsRoot = Path.Combine(workspace, ".harness", "runs");
        if (!Directory.Exists(runsRoot)) return null;
        var dir = Directory.EnumerateDirectories(runsRoot, $"*-{id}").FirstOrDefault()
                  ?? Directory.EnumerateDirectories(runsRoot, $"*{id}*").FirstOrDefault();
        if (dir is null) return null;
        var path = Path.Combine(dir, "report.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RunReport>(File.ReadAllText(path), Options); }
        catch { return null; }
    }

    private static RunMetrics Metrics(RunReport report)
    {
        var results = report.Results;
        var loadSkillCalls = results.Sum(r => r.Trace.Count(t => t.Tool.Contains("load_skill", StringComparison.OrdinalIgnoreCase)));
        var distinctLoaded = results.SelectMany(r => r.LoadedSkills ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new RunMetrics(
            results.Count,
            results.Count(r => r.Error is null),
            results.Count(r => r.Error is not null),
            results.Sum(r => r.Trace.Count),
            report.AdvertisedSkills?.Count ?? 0,
            distinctLoaded,
            loadSkillCalls,
            results.Sum(r => r.TokenUsage?.Prompt ?? 0),
            results.Sum(r => r.TokenUsage?.Completion ?? 0),
            results.Sum(r => r.TokenUsage?.Total ?? 0),
            results.Sum(r => r.TokenUsage?.Cached ?? 0),
            results.Sum(r => r.TokenUsage?.Cost ?? 0),
            report.DurationMs);
    }

    private static IReadOnlyList<TestDelta> Changes(RunReport before, RunReport after)
    {
        var deltas = new List<TestDelta>();
        var beforeByPath = new Dictionary<string, TestResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in before.Results) beforeByPath[r.Test.Path] = r;

        foreach (var a in after.Results)
        {
            if (!beforeByPath.TryGetValue(a.Test.Path, out var b))
            {
                deltas.Add(new TestDelta(a.Test.Name, "new-test", 0, a.Trace.Count));
                continue;
            }
            var wasError = b.Error is not null;
            var isError = a.Error is not null;
            if (wasError && !isError) deltas.Add(new TestDelta(a.Test.Name, "fixed", b.Trace.Count, a.Trace.Count));
            else if (!wasError && isError) deltas.Add(new TestDelta(a.Test.Name, "regressed", b.Trace.Count, a.Trace.Count));
            else if (b.Trace.Count != a.Trace.Count) deltas.Add(new TestDelta(a.Test.Name, "tool-calls-changed", b.Trace.Count, a.Trace.Count));
        }

        var afterPaths = after.Results.Select(r => r.Test.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in before.Results.Where(r => !afterPaths.Contains(r.Test.Path)))
            deltas.Add(new TestDelta(b.Test.Name, "removed", b.Trace.Count, 0));

        return deltas;
    }
}
