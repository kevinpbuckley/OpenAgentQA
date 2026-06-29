using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenHarness.Api;

public sealed class RunCoordinator(HarnessConfiguration configuration, AgentRuntime agentRuntime, IHubContext<RunHub> hub, ILogger<RunCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, RunJob> _jobs = new();

    public IReadOnlyList<string> DiscoverTests()
    {
        var config = configuration.Load();
        var directory = Path.GetFullPath(Path.Combine(configuration.Workspace, config.TestDir));
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Order().ToList()
            : [];
    }

    public RunJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IReadOnlyList<RunListItem> ListRuns()
    {
        var root = RunsRoot();
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(directory =>
        {
            var metadata = Path.Combine(directory, "run.json");
            if (!File.Exists(metadata)) return new RunListItem(Path.GetFileName(directory), "incomplete", Directory.GetCreationTimeUtc(directory), 0, 0, null);
            using var document = JsonDocument.Parse(File.ReadAllText(metadata));
            var rootElement = document.RootElement;
            return new RunListItem(
                Property(rootElement, "id").GetString() ?? Path.GetFileName(directory),
                Property(rootElement, "status").GetString() ?? "unknown",
                Property(rootElement, "startedAt").GetDateTimeOffset(),
                Property(rootElement, "total").GetInt32(),
                Property(rootElement, "completed").GetInt32(),
                Property(rootElement, "error").ValueKind is JsonValueKind.Null ? null : Property(rootElement, "error").GetString());
        }).OrderByDescending(run => run.StartedAt).ToList();
    }

    public IResult? GetRunArtifact(string id, string artifact)
    {
        var directory = ResolveRunDirectory(id);
        if (directory is null) return null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run.json", "report.json", "system.log", "chat.jsonl", "issues.json", "manifest.json" };
        if (!allowed.Contains(artifact)) return Results.BadRequest(new { error = "Unknown artifact" });
        var path = Path.Combine(directory, artifact);
        return File.Exists(path) ? Results.File(path, artifact.EndsWith(".log") || artifact.EndsWith(".jsonl") ? "text/plain; charset=utf-8" : "application/json") : Results.NotFound();
    }

    public IResult? GetPromptArtifact(string id, string relativePath)
    {
        var directory = ResolveRunDirectory(id);
        if (directory is null) return null;
        var root = Path.GetFullPath(Path.Combine(directory, "prompts"));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
        var type = path.EndsWith(".md") || path.EndsWith(".log") || path.EndsWith(".jsonl") ? "text/plain; charset=utf-8" : "application/json";
        return Results.File(path, type);
    }

    public IResult? DownloadRun(string id)
    {
        var directory = ResolveRunDirectory(id);
        if (directory is null) return null;
        var exportDirectory = Path.Combine(configuration.Workspace, ".harness", "exports");
        Directory.CreateDirectory(exportDirectory);
        var archive = Path.Combine(exportDirectory, $"{id}.zip");
        if (File.Exists(archive)) File.Delete(archive);
        ZipFile.CreateFromDirectory(directory, archive, CompressionLevel.Fastest, includeBaseDirectory: true);
        return Results.File(archive, "application/zip", $"openharness-run-{id}.zip");
    }

    /// <summary>Open the run's artifact directory in the OS file browser (server-side = the user's box).</summary>
    public bool OpenRun(string id)
    {
        var directory = ResolveRunDirectory(id);
        if (directory is null) return false;
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open", ArgumentList = { directory }, UseShellExecute = true });
        }
        catch (Exception error) { logger.LogWarning(error, "Could not open run directory {Directory}", directory); }
        return true;
    }

    public bool Delete(string id)
    {
        if (_jobs.TryGetValue(id, out var active) && active.Status == "running") active.Cancellation.Cancel();
        var directory = ResolveRunDirectory(id);
        if (directory is null) return false;
        Directory.Delete(directory, recursive: true);
        _jobs.TryRemove(id, out _);
        return true;
    }

    public RunJob Start(RunRequest request)
    {
        var (job, config) = CreateJob(request);
        _ = ExecuteAsync(job, request, config);
        return job;
    }

    /// <summary>Run to completion and return the report. Used by the headless CLI.</summary>
    public async Task<RunReport> RunToCompletionAsync(RunRequest request)
    {
        var (job, config) = CreateJob(request);
        await ExecuteAsync(job, request, config);
        return job.Report ?? new RunReport(job.StartedAt, 0, job.Total, [], [], config);
    }

    private (RunJob Job, HarnessConfig Config) CreateJob(RunRequest request)
    {
        var config = configuration.Load();
        var files = request.Files?.Count > 0 ? request.Files : DiscoverTests();
        var id = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..24];
        var job = new RunJob
        {
            Id = id,
            StartedAt = DateTimeOffset.UtcNow,
            Total = files.Count,
            Tests = files.Select(path => new RunJobTest { Path = path, Name = Path.GetFileNameWithoutExtension(path), ArtifactPath = PromptArtifactName(path) }).ToList(),
            Directory = Path.Combine(RunsRoot(), $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}-{id}"),
        };
        Directory.CreateDirectory(job.Directory);
        foreach (var entry in job.Tests) WritePromptSource(job, entry);
        WriteSystemLog(job, $"Run started with {files.Count} test(s), requested parallelism {request.Parallel ?? config.Parallel}.");
        SaveJob(job);
        _jobs[job.Id] = job;
        return (job, config);
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.Status != "running") return false;
        job.Cancellation.Cancel();
        return true;
    }

    private async Task ExecuteAsync(RunJob job, RunRequest request, HarnessConfig config)
    {
        var started = Stopwatch.StartNew();
        var results = new ConcurrentBag<TestResult>();
        var workerCount = Math.Clamp(request.Parallel ?? config.Parallel, 1, 20);
        try
        {
            await Parallel.ForEachAsync(job.Tests, new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = job.Cancellation.Token,
            }, async (entry, cancellationToken) =>
            {
                entry.Status = "running";
                WriteSystemLog(job, $"Test started: {entry.Name}");
                WritePromptLog(job, entry, "Prompt execution started.");
                await PublishAsync(job);
                var test = ReadTest(entry.Path);
                var startedAt = DateTimeOffset.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var chat = await agentRuntime.RunAsync(test.Prompt, null, test.ModelOverride ?? request.Model, test.TemperatureOverride, cancellationToken);
                    var result = new TestResult(test, chat.Response, stopwatch.ElapsedMilliseconds, chat.ToolCalls, chat.TokenUsage, null, startedAt, DateTimeOffset.UtcNow, chat.Conversation);
                    results.Add(result);
                    entry.Status = "completed";
                    entry.DurationMs = result.DurationMs;
                    WriteChatLog(job, result);
                    WritePromptArtifacts(job, entry, result);
                    WriteSystemLog(job, $"Test completed: {entry.Name} in {result.DurationMs}ms.");
                }
                catch (Exception error)
                {
                    var result = new TestResult(test, string.Empty, stopwatch.ElapsedMilliseconds, [], null, error.Message, startedAt, DateTimeOffset.UtcNow);
                    results.Add(result);
                    entry.Status = "failed";
                    entry.DurationMs = result.DurationMs;
                    entry.Error = error.Message;
                    WriteChatLog(job, result);
                    WritePromptArtifacts(job, entry, result);
                    WriteSystemLog(job, $"Test failed: {entry.Name} — {error.Message}");
                    var issuesDir = Path.Combine(configuration.Workspace, ".harness", "issues");
                    if (!IssueStore.HasOpenForTest(issuesDir, test.Name))
                        IssueStore.Create(issuesDir, test.Name, "error", error.Message, error.ToString());
                }
                finally
                {
                    lock (job) job.Completed++;
                    await PublishAsync(job);
                }
            });

            var orderedResults = results.OrderBy(result => result.Test.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var logs = SaveLogs(orderedResults);
            job.Report = new RunReport(job.StartedAt, started.ElapsedMilliseconds, orderedResults.Count, logs, orderedResults, config);
            job.Status = "completed";
            SaveReport(job.Report);
            File.WriteAllText(Path.Combine(job.Directory, "report.json"), JsonSerializer.Serialize(job.Report, JsonOptions));
            File.WriteAllText(Path.Combine(job.Directory, "issues.json"), JsonSerializer.Serialize(orderedResults.Where(result => result.Error is not null).Select(result => new { test = result.Test.Name, error = result.Error, durationMs = result.DurationMs }), JsonOptions));
            File.WriteAllText(Path.Combine(job.Directory, "manifest.json"), JsonSerializer.Serialize(job.Tests.Select(entry => new { entry.Name, entry.Path, entry.ArtifactPath, entry.Status, entry.DurationMs, entry.Error }), JsonOptions));
            WriteSystemLog(job, $"Run completed: {job.Completed}/{job.Total} tests finished.");
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.Error = "Run cancelled";
            WriteSystemLog(job, "Run cancelled.");
        }
        catch (Exception error)
        {
            logger.LogError(error, "Run job {JobId} failed", job.Id);
            job.Status = "failed";
            job.Error = error.Message;
            WriteSystemLog(job, $"Run failed: {error.Message}");
        }
        finally
        {
            SaveJob(job);
            await PublishAsync(job);
        }
    }

    /// <summary>
    /// Run the configured PowerShell setup script on demand to spin up a clean testing
    /// environment. Invoked manually (e.g. from the "Spin up clean instance" button), not
    /// automatically at run start. Streams each stdout/stderr line through <paramref name="onLine"/>
    /// as it is produced (so the UI shows live progress during the long build), and returns the
    /// exit-code result rather than throwing so the caller can surface it.
    /// </summary>
    public async Task<SetupScriptResult> RunSetupScriptAsync(Func<string, Task> onLine, CancellationToken cancellationToken)
    {
        var config = configuration.Load();
        if (string.IsNullOrWhiteSpace(config.SetupScript))
            return new SetupScriptResult(false, null, "", "", "No setupScript is configured.");

        var scriptPath = Path.GetFullPath(Path.Combine(configuration.Workspace, config.SetupScript));
        if (!File.Exists(scriptPath))
            return new SetupScriptResult(false, null, "", "", $"Setup script not found: {scriptPath}");

        // Prefer PowerShell 7+ (pwsh, cross-platform); fall back to Windows PowerShell.
        var candidates = OperatingSystem.IsWindows() ? new[] { "pwsh", "powershell" } : ["pwsh"];
        Process? process = null;
        foreach (var shell in candidates)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = configuration.Workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                startInfo.ArgumentList.Add(arg);
            try { process = Process.Start(startInfo); break; }
            catch (System.ComponentModel.Win32Exception) { /* shell not found — try the next */ }
        }
        if (process is null)
            return new SetupScriptResult(false, null, "", "", $"Could not launch a PowerShell host ({string.Join(", ", candidates)}) to run the setup script.");

        using (process)
        {
            // Pump stdout + stderr line-by-line into a channel so they interleave in arrival
            // order and reach the caller (and the browser) as the script runs.
            var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();
            async Task Pump(StreamReader reader)
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                    channel.Writer.TryWrite(line);
            }
            var readers = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
            _ = readers.ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);

            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken))
                await onLine(line);

            await readers;
            await process.WaitForExitAsync(cancellationToken);
            var ok = process.ExitCode == 0;
            return new SetupScriptResult(ok, process.ExitCode, "", "",
                ok ? null : $"Setup script exited with code {process.ExitCode}.");
        }
    }

    private TestCase ReadTest(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return PromptParser.Parse(fullPath, File.ReadAllText(fullPath));
    }

    private IReadOnlyList<string> SaveLogs(IReadOnlyList<TestResult> results)
    {
        var directory = Path.Combine(configuration.Workspace, ".harness", "logs");
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        return results.Select((result, index) =>
        {
            var name = string.Concat(result.Test.Name.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
            var path = Path.Combine(directory, $"{stamp}-{index:D3}-{name}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
            return path;
        }).ToList();
    }

    private void SaveReport(RunReport report)
    {
        var directory = Path.Combine(configuration.Workspace, ".harness", "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"report-{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    // camelCase to match the live ASP.NET API (and the UI), so file-backed reads of these
    // artifacts deserialize with the same property names as the in-memory run.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions ChatJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static JsonElement Property(JsonElement element, string name) => element.TryGetProperty(name, out var value) || element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value) ? value : default;
    private string RunsRoot() => Path.Combine(configuration.Workspace, ".harness", "runs");
    private string? ResolveRunDirectory(string id)
    {
        if (id.Any(character => !char.IsLetterOrDigit(character) && character != '-')) return null;
        return Directory.EnumerateDirectories(RunsRoot(), $"*-{id}").FirstOrDefault();
    }
    private static void WriteSystemLog(RunJob job, string message)
    {
        lock (job) File.AppendAllText(Path.Combine(job.Directory, "system.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }
    private static void WriteChatLog(RunJob job, TestResult result)
    {
        var entry = JsonSerializer.Serialize(new { timestamp = result.CompletedAt, startedAt = result.StartedAt, completedAt = result.CompletedAt, durationMs = result.DurationMs, test = result.Test.Name, prompt = result.Test.Prompt, response = result.Response, toolCalls = result.Trace, tokenUsage = result.TokenUsage, error = result.Error }, ChatJsonOptions);
        lock (job) File.AppendAllText(Path.Combine(job.Directory, "chat.jsonl"), entry + Environment.NewLine);
    }
    private static void WritePromptSource(RunJob job, RunJobTest entry)
    {
        var directory = Path.Combine(job.Directory, "prompts", entry.ArtifactPath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "prompt.md"), $"# {entry.Name}\n\n**Source:** `{entry.Path}`\n\n{File.ReadAllText(entry.Path).Trim()}\n");
        File.WriteAllText(Path.Combine(directory, "system.log"), $"{DateTimeOffset.UtcNow:O} Prompt artifact created.{Environment.NewLine}");
    }
    private static void WritePromptLog(RunJob job, RunJobTest entry, string message)
    {
        lock (job) File.AppendAllText(Path.Combine(job.Directory, "prompts", entry.ArtifactPath, "system.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }
    private static void WritePromptArtifacts(RunJob job, RunJobTest entry, TestResult result)
    {
        var directory = Path.Combine(job.Directory, "prompts", entry.ArtifactPath);
        var chat = JsonSerializer.Serialize(new { timestamp = result.CompletedAt, startedAt = result.StartedAt, completedAt = result.CompletedAt, durationMs = result.DurationMs, prompt = result.Test.Prompt, response = result.Response, toolCalls = result.Trace, tokenUsage = result.TokenUsage, error = result.Error }, ChatJsonOptions);
        File.WriteAllText(Path.Combine(directory, "chat.jsonl"), chat + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, JsonOptions));
        if (result.Error is not null) File.WriteAllText(Path.Combine(directory, "issue.json"), JsonSerializer.Serialize(new { test = result.Test.Name, error = result.Error, durationMs = result.DurationMs }, JsonOptions));
        WritePromptLog(job, entry, result.Error is null ? $"Prompt completed in {result.DurationMs}ms." : $"Prompt failed after {result.DurationMs}ms: {result.Error}");
    }
    private static string PromptArtifactName(string path)
    {
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(path).Select(character => char.IsLetterOrDigit(character) ? character : '-')).Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..8].ToLowerInvariant();
        return $"{safeName}-{hash}";
    }
    private static void SaveJob(RunJob job) => File.WriteAllText(Path.Combine(job.Directory, "run.json"), JsonSerializer.Serialize(job, JsonOptions));

    private Task PublishAsync(RunJob job) => hub.Clients.Group(job.Id).SendAsync("runProgress", job);
}
