using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace OpenHarness.Api;

/// <summary>Headless CLI commands (run / list / init / issues) for the unified tool.</summary>
internal static class Cli
{
    public static Task<int> RunAsync(string command, string[] args, string workspace) => command switch
    {
        "run" => RunCommand(args, workspace),
        "list" => Task.FromResult(ListCommand(args, workspace)),
        "init" => Task.FromResult(InitCommand(workspace)),
        "issues" => Task.FromResult(IssuesCommand(args, workspace)),
        "compare" => Task.FromResult(CompareCommand(args, workspace)),
        _ => Task.FromResult(Unknown(command)),
    };

    private static async Task<int> RunCommand(string[] args, string workspace)
    {
        var options = ParseOptions(args);
        var output = options.Get("output") ?? "console";
        var reportPath = options.Get("report");
        var model = options.Get("model");

        var files = options.Files.Select(f => Path.GetFullPath(Path.Combine(workspace, f))).ToList();
        foreach (var positional in options.Positionals)
        {
            var dir = Path.GetFullPath(Path.Combine(workspace, positional));
            if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories));
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new HarnessConfiguration(workspace));
        builder.Services.AddSingleton<AgentRuntime>();
        builder.Services.AddSingleton<RunCoordinator>();
        await using var app = builder.Build();
        var coordinator = app.Services.GetRequiredService<RunCoordinator>();

        Console.WriteLine("\n  OpenHarness — running tests via Microsoft Agent Framework\n");
        var request = new RunRequest(files.Count > 0 ? files : null, null, model);
        var job = await coordinator.RunToCompletionAsync(request);
        var report = job.Report!;

        switch (output)
        {
            case "json":
                WriteOrPrint(workspace, reportPath, Reports.Json(report), "Report");
                break;
            case "markdown":
                WriteOrPrint(workspace, reportPath, Reports.Markdown(report), "Report");
                break;
            case "junit":
                WriteOrPrint(workspace, reportPath, Reports.Junit(report), "JUnit report");
                break;
            default:
                Reports.Console(report);
                break;
        }

        if (reportPath is not null && output != "json")
        {
            var basePath = StripExtension(reportPath);
            File.WriteAllText(Path.GetFullPath(Path.Combine(workspace, basePath + ".json")), Reports.Json(report));
            Console.WriteLine($"JSON report written to {basePath}.json");
        }

        Console.WriteLine($"\n  Logs saved to .harness/logs/   Run id: {job.Id}");
        Console.WriteLine($"  Compare against another run:  compare {job.Id} <other-run-id>\n");
        return 0;
    }

    private static int CompareCommand(string[] args, string workspace)
    {
        var options = ParseOptions(args);
        if (options.Positionals.Count < 2)
        {
            Console.Error.WriteLine("Usage: compare <before-run-id> <after-run-id>");
            return 1;
        }
        var comparison = RunComparer.Compare(workspace, options.Positionals[0], options.Positionals[1]);
        if (comparison is null)
        {
            Console.Error.WriteLine("One or both runs not found under .harness/runs/.");
            return 1;
        }
        Reports.ConsoleComparison(comparison);
        return 0;
    }

    private static int ListCommand(string[] args, string workspace)
    {
        var options = ParseOptions(args);
        var config = new HarnessConfiguration(workspace).Load();
        var positional = options.Positionals.FirstOrDefault();
        var dir = Path.GetFullPath(Path.Combine(workspace, positional ?? config.TestDir));
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Order().ToList()
            : [];

        if (files.Count == 0)
        {
            Console.WriteLine("No test files found.");
            return 0;
        }
        Console.WriteLine($"\n  Test cases in {dir}:\n");
        foreach (var file in files) Console.WriteLine($"  • {file}");
        Console.WriteLine("");
        return 0;
    }

    private static int IssuesCommand(string[] args, string workspace)
    {
        var baseDir = Path.Combine(workspace, ".harness", "issues");
        var sub = args.Length > 0 ? args[0] : "list";
        var rest = args.Length > 0 ? args[1..] : args;
        var options = ParseOptions(rest);

        switch (sub)
        {
            case "list":
            {
                var all = IssueStore.List(baseDir);
                if (all.Count == 0) { Console.WriteLine("\n  No issues found.\n"); return 0; }
                Reports.ConsoleIssues(options.Get("all") is not null ? all : all.Where(i => i.Status == "open").ToList());
                return 0;
            }
            case "show":
            {
                var id = options.Positionals.FirstOrDefault();
                var issue = id is null ? null : IssueStore.Get(baseDir, id);
                if (issue is null) { Console.WriteLine($"\n  Issue \"{id}\" not found.\n"); return 0; }
                Console.WriteLine($"\n  {issue.Id} [{issue.Severity}] — {issue.Status}");
                Console.WriteLine($"  Test: {issue.Test}");
                Console.WriteLine($"  Created: {issue.Created}");
                if (issue.Resolved is not null) Console.WriteLine($"  Resolved: {issue.Resolved}");
                Console.WriteLine($"  Summary: {issue.Summary}");
                Console.WriteLine($"  Trace:\n{Truncate(issue.Trace, 1000)}\n");
                return 0;
            }
            case "resolve":
            {
                var id = options.Positionals.FirstOrDefault();
                var resolved = id is null ? null : IssueStore.Resolve(baseDir, id);
                Console.WriteLine(resolved is null ? $"\n  Issue \"{id}\" not found.\n" : $"\n  {id} resolved.\n");
                return 0;
            }
            case "summary":
            {
                var all = IssueStore.List(baseDir);
                var open = all.Count(i => i.Status == "open");
                Console.WriteLine($"\n  Issues: {all.Count} total, {open} open, {all.Count - open} resolved\n");
                return 0;
            }
            default:
                Console.WriteLine($"Unknown issues subcommand: {sub}");
                return 1;
        }
    }

    private static int InitCommand(string workspace)
    {
        var indented = new JsonSerializerOptions { WriteIndented = true };

        var configPath = Path.Combine(workspace, "open-agent-qa.json");
        if (!File.Exists(configPath))
        {
            var config = new
            {
                provider = "openrouter",
                model = "openai/gpt-4o",
                agentsDir = "./Agents",
                agent = "Sample",
                testDir = "./tests",
                temperature = 0.3,
                maxSteps = 20,
                parallel = 3,
                tracker = new { enabled = true, dir = ".harness/issues" },
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(new Dictionary<string, object> { ["open-agent-qa"] = config }, indented));
        }

        // Scaffold a Sample agent (self-contained: agent.md + skills + mcp + its own tests).
        var agentDir = Path.Combine(workspace, "Agents", "Sample");
        Directory.CreateDirectory(Path.Combine(agentDir, "skills"));
        var agentTests = Path.Combine(agentDir, "tests");
        Directory.CreateDirectory(agentTests);

        WriteIfMissing(Path.Combine(agentDir, "agent.json"),
            JsonSerializer.Serialize(new { name = "Sample", description = "Starter agent — copy this folder to make your own.", agentMd = "agent.md", skillsDir = "skills", mcp = "mcp.json", testDir = "tests" }, indented));
        WriteIfMissing(Path.Combine(agentDir, "agent.md"),
            "# Sample Agent\n\nYou are a helpful AI assistant being tested. No MCP tools are configured, so you run as a plain LLM.\n\n## Guidelines\n- Be concise and direct.\n- Follow the user's formatting instructions exactly.\n");
        WriteIfMissing(Path.Combine(agentDir, "mcp.json"),
            JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>() }, indented));
        WriteIfMissing(Path.Combine(agentDir, "skills", "example-skill.md"),
            "# Example Skill\n\nSkill files are appended to the system prompt (sorted). Replace this with your own rules/knowledge.\n");
        WriteIfMissing(Path.Combine(agentTests, "hello-world.md"), "Reply with exactly \"Hello, World!\" and nothing else.\n");
        WriteIfMissing(Path.Combine(agentTests, "capitalize.md"), "Capitalize the following: \"hello world, this is a test.\"\n");

        Console.WriteLine("\n  OpenHarness initialized.\n");
        Console.WriteLine("  • Agent scaffolded at Agents/Sample/ (copy it to make your own).");
        Console.WriteLine("  • Set OPENROUTER_API_KEY (or a .env file), then `run` to start.\n");
        return 0;
    }

    private static void WriteOrPrint(string workspace, string? reportPath, string content, string label)
    {
        if (reportPath is not null)
        {
            File.WriteAllText(Path.GetFullPath(Path.Combine(workspace, reportPath)), content);
            Console.WriteLine($"{label} written to {reportPath}");
        }
        else
        {
            Console.WriteLine(content);
        }
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    private static string StripExtension(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Commands: run, list, init, issues, serve");
        return 1;
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg)
            {
                case "-f" or "--file": { var value = Next(); if (value is not null) options.Files.Add(value); break; }
                case "-o" or "--output": options.Set("output", Next()); break;
                case "--report": options.Set("report", Next()); break;
                case "--model": options.Set("model", Next()); break;
                case "-a" or "--all": options.Set("all", "true"); break;
                default:
                    if (arg.StartsWith('-')) options.Set(arg.TrimStart('-'), Next());
                    else options.Positionals.Add(arg);
                    break;
            }
        }
        return options;
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = [];
        public List<string> Files { get; } = [];
        public void Set(string name, string? value) => _flags[name] = value;
        public string? Get(string name) => _flags.TryGetValue(name, out var value) ? value ?? "true" : null;
    }
}
