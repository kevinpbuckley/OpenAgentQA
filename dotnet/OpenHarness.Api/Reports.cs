using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenHarness.Api;

/// <summary>Console and file report formatters for the CLI.</summary>
internal static class Reports
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string FormatCost(double cost) =>
        "$" + cost.ToString(cost < 0.01 ? "F6" : "F4", CultureInfo.InvariantCulture);

    public static string Json(RunReport report) => JsonSerializer.Serialize(report, Indented);

    public static void Console(RunReport report)
    {
        var output = System.Console.Out;
        output.WriteLine("\n═══════════════════════════════════════");
        output.WriteLine("  OpenHarness — Run Results");
        output.WriteLine("═══════════════════════════════════════\n");

        foreach (var result in report.Results)
        {
            output.WriteLine($"  {(result.Error is not null ? "⚠" : "✓")} {result.Test.Name}");
            output.WriteLine($"     File: {result.Test.Path}");
            output.WriteLine($"     Duration: {result.DurationMs}ms");

            if (result.TokenUsage is { } u)
            {
                var cached = u.Cached > 0 ? $" · {u.Cached} cached" : "";
                var cost = u.Cost > 0 ? $" · {FormatCost(u.Cost)}" : "";
                output.WriteLine($"     Tokens: {u.Prompt}↑ {u.Completion}↓ ({u.Total}){cached}{cost}");
            }

            if (result.Trace.Count > 0)
            {
                output.WriteLine($"     Tool calls: {result.Trace.Count}");
                foreach (var trace in result.Trace.TakeLast(3))
                {
                    var input = JsonSerializer.Serialize(trace.Input);
                    output.WriteLine($"       → {trace.Tool}({Truncate(input, 80)})");
                }
            }

            if (result.Error is not null) output.WriteLine($"     Error: {result.Error}");
            output.WriteLine("");
        }

        output.WriteLine("───────────────────────────────────────");
        output.WriteLine($"  Total: {report.Total}");
        if (report.DurationMs > 0 && report.Total > 0)
            output.WriteLine($"  Avg: {report.DurationMs / report.Total}ms");

        long totalPrompt = 0, totalCompletion = 0, totalCached = 0;
        double totalCost = 0;
        foreach (var result in report.Results)
            if (result.TokenUsage is { } u)
            {
                totalPrompt += u.Prompt;
                totalCompletion += u.Completion;
                totalCached += u.Cached;
                totalCost += u.Cost;
            }
        if (totalPrompt > 0 || totalCompletion > 0)
        {
            var hitRate = totalPrompt > 0 ? (int)Math.Round(totalCached / (double)totalPrompt * 100) : 0;
            var cost = totalCost > 0 ? $" · {FormatCost(totalCost)} total" : "";
            output.WriteLine($"  Tokens: {totalPrompt}↑ {totalCompletion}↓ · {totalCached} cached ({hitRate}% of input){cost}");
        }
        output.WriteLine("═══════════════════════════════════════\n");
    }

    public static string Markdown(RunReport report)
    {
        var md = new StringBuilder();
        md.Append("# OpenHarness Run Log\n\n");
        md.Append($"**Date:** {report.StartedAt:O}\n");
        md.Append($"**Total:** {report.Total}\n\n");
        md.Append("| Prompt | Duration | Tokens | Cached | Cost | Tool Calls | Error | Log |\n");
        md.Append("|--------|----------|--------|--------|------|------------|-------|-----|\n");

        for (var i = 0; i < report.Results.Count; i++)
        {
            var r = report.Results[i];
            var tokens = r.TokenUsage is { } u ? u.Total.ToString() : "-";
            var cached = r.TokenUsage is { Cached: > 0 } c ? c.Cached.ToString() : "-";
            var cost = r.TokenUsage is { Cost: > 0 } cc ? FormatCost(cc.Cost) : "-";
            var toolCalls = r.Trace.Count > 0 ? r.Trace.Count.ToString() : "-";
            var error = r.Error is not null ? "YES" : "-";
            var logName = i < report.Logs.Count ? Path.GetFileName(report.Logs[i]) : "-";
            md.Append($"| {r.Test.Name} | {r.DurationMs}ms | {tokens} | {cached} | {cost} | {toolCalls} | {error} | {logName} |\n");
        }
        return md.ToString();
    }

    public static string Junit(RunReport report)
    {
        var failures = report.Results.Count(r => r.Error is not null);
        var totalSeconds = (report.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        xml.Append($"<testsuites name=\"OpenHarness\" tests=\"{report.Total}\" failures=\"{failures}\" time=\"{totalSeconds}\">\n");
        xml.Append($"  <testsuite name=\"OpenHarness\" tests=\"{report.Total}\" failures=\"{failures}\" time=\"{totalSeconds}\" timestamp=\"{XmlEscape(report.StartedAt.ToString("O"))}\">\n");
        foreach (var r in report.Results)
        {
            var time = (r.DurationMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
            var name = XmlEscape(r.Test.Name);
            var file = XmlEscape(r.Test.Path);
            if (r.Error is not null)
            {
                xml.Append($"    <testcase name=\"{name}\" classname=\"{name}\" file=\"{file}\" time=\"{time}\">\n");
                xml.Append($"      <failure message=\"{XmlEscape(r.Error)}\">{XmlEscape(r.Error)}</failure>\n");
                xml.Append("    </testcase>\n");
            }
            else
            {
                xml.Append($"    <testcase name=\"{name}\" classname=\"{name}\" file=\"{file}\" time=\"{time}\" />\n");
            }
        }
        xml.Append("  </testsuite>\n");
        xml.Append("</testsuites>\n");
        return xml.ToString();
    }

    public static void ConsoleComparison(RunComparison c)
    {
        var output = System.Console.Out;
        output.WriteLine("\n=======================================");
        output.WriteLine("  OpenHarness - Run Comparison (scoreboard)");
        output.WriteLine("=======================================");
        output.WriteLine($"  before: {c.RunBefore}");
        output.WriteLine($"  after:  {c.RunAfter}\n");

        void Row(string label, double before, double after, bool higherIsBetter, string fmt = "0.####")
        {
            var delta = after - before;
            var good = delta == 0 ? "" : (delta > 0) == higherIsBetter ? "  (better)" : "  (worse)";
            var deltaStr = delta == 0 ? "" : $"   {(delta > 0 ? "+" : "")}{delta.ToString(fmt, CultureInfo.InvariantCulture)}{good}";
            output.WriteLine($"  {label,-22} {before.ToString(fmt, CultureInfo.InvariantCulture),12} -> {after.ToString(fmt, CultureInfo.InvariantCulture),-12}{deltaStr}");
        }

        var b = c.Before; var a = c.After;
        Row("Tests passed", b.Passed, a.Passed, true);
        Row("Tests errored", b.Errored, a.Errored, false);
        Row("Tool calls", b.ToolCalls, a.ToolCalls, false);
        Row("Skills advertised", b.AdvertisedSkills, a.AdvertisedSkills, true);
        Row("Distinct skills loaded", b.DistinctSkillsLoaded, a.DistinctSkillsLoaded, true);
        Row("load_skill calls", b.LoadSkillCalls, a.LoadSkillCalls, true);
        Row("Prompt tokens", b.PromptTokens, a.PromptTokens, false, "0");
        Row("Completion tokens", b.CompletionTokens, a.CompletionTokens, false, "0");
        Row("Cached tokens", b.CachedTokens, a.CachedTokens, true, "0");
        Row("Cost (USD)", b.Cost, a.Cost, false, "0.######");
        Row("Duration (ms)", b.DurationMs, a.DurationMs, false, "0");

        if (c.Changes.Count > 0)
        {
            output.WriteLine("\n  Per-test changes:");
            foreach (var d in c.Changes)
                output.WriteLine($"    {d.Change,-20} {d.Test}   (tool calls {d.ToolCallsBefore} -> {d.ToolCallsAfter})");
        }
        else
        {
            output.WriteLine("\n  No per-test status changes.");
        }
        output.WriteLine("=======================================\n");
    }

    public static void ConsoleIssues(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            System.Console.WriteLine("\n  No open issues.\n");
            return;
        }
        System.Console.WriteLine("\n═══════════════════════════════════════");
        System.Console.WriteLine("  OpenHarness — Open Issues");
        System.Console.WriteLine("═══════════════════════════════════════\n");
        foreach (var issue in issues)
        {
            System.Console.WriteLine($"  {(issue.Severity == "error" ? "⚠" : "✗")} {issue.Id} [{issue.Severity}] {issue.Test}");
            System.Console.WriteLine($"     {issue.Summary}");
            System.Console.WriteLine($"     Created: {issue.Created}");
            System.Console.WriteLine("");
        }
        System.Console.WriteLine($"  Total open: {issues.Count}");
        System.Console.WriteLine("═══════════════════════════════════════\n");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
