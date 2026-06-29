using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenHarness.Api;

/// <summary>
/// Parses a test markdown file into a <see cref="TestCase"/>. Structured files may
/// carry YAML frontmatter (name + per-test config) and a <c>## Prompt</c> section; only
/// that section is sent to the agent, while <c>## Expected</c>/<c>## Assert</c> are kept
/// for the reviewer and never reach the agent under test. Freeform files (no frontmatter,
/// no <c>## Prompt</c>) are sent whole, unchanged.
/// </summary>
internal static class PromptParser
{
    private static readonly Regex Frontmatter =
        new(@"\A---\r?\n(.*?)\r?\n---\r?\n?", RegexOptions.Singleline | RegexOptions.Compiled);

    public static TestCase Parse(string path, string raw)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        string? name = null, model = null;
        double? temperature = null;
        var body = raw;

        var frontmatter = Frontmatter.Match(raw);
        if (frontmatter.Success)
        {
            body = raw[frontmatter.Length..];
            ReadFrontmatter(frontmatter.Groups[1].Value, ref name, ref model, ref temperature);
        }
        body = body.Trim();

        var prompt = (ExtractSection(body, "Prompt") ?? body).Trim();
        var expected = ExtractSection(body, "Expected");
        var asserts = ExtractSection(body, "Assert") ?? ExtractSection(body, "Assertions");

        return new TestCase(path, name ?? fileName, prompt, expected, asserts, model, temperature);
    }

    /// <summary>Convenience for callers that only need the agent-facing prompt text.</summary>
    public static string ExtractPrompt(string raw) => Parse("untitled.md", raw).Prompt;

    private static void ReadFrontmatter(string yaml, ref string? name, ref string? model, ref double? temperature)
    {
        var inConfig = false;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var indented = line[0] is ' ' or '\t';
            var trimmed = line.Trim();

            if (!indented && trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = Unquote(trimmed["name:".Length..].Trim());
            else if (!indented && trimmed.StartsWith("config:", StringComparison.OrdinalIgnoreCase))
                inConfig = true;
            else if (!indented)
                inConfig = false; // a new top-level key closes the config block
            else if (inConfig && trimmed.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                model = Unquote(trimmed["model:".Length..].Trim());
            else if (inConfig && trimmed.StartsWith("temperature:", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(trimmed["temperature:".Length..].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var t))
                temperature = t;
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static string? ExtractSection(string body, string heading)
    {
        var headingMatch = Regex.Match(body, $@"^##\s+{Regex.Escape(heading)}\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!headingMatch.Success) return null;

        var rest = body[(headingMatch.Index + headingMatch.Length)..];
        var next = Regex.Match(rest, @"^#{1,2}\s+", RegexOptions.Multiline);
        var text = (next.Success ? rest[..next.Index] : rest).Trim();
        return text.Length > 0 ? text : null;
    }
}
