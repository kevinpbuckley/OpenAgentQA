using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using OpenHarness.Api;
using System.Text.Json;
using System.Text.Json.Nodes;

var workspace = FindWorkspace(Directory.GetCurrentDirectory());

// Headless CLI commands run and exit; "serve" (or no args) starts the web UI.
var command = args.Length > 0 ? args[0] : "serve";
if (command is "run" or "list" or "init" or "issues")
    return await Cli.RunAsync(command, args[1..], workspace);
if (command is not "serve")
{
    await Console.Error.WriteLineAsync($"Unknown command: {command}");
    await Console.Error.WriteLineAsync("Commands: run, list, init, issues, serve");
    return 1;
}

var hostArgs = args.Length > 0 && args[0] == "serve" ? args[1..] : args;
var builder = WebApplication.CreateBuilder(hostArgs);
builder.Services.AddSingleton(new HarnessConfiguration(workspace));
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton<RunCoordinator>();
builder.Services.AddSignalR();

var app = builder.Build();
var uiDirectory = Path.Combine(workspace, "ui");
var contentTypes = new FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uiDirectory),
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store",
});

// A fresh id per process start; the UI polls this and reloads itself when it changes, so a
// long-open SPA tab never keeps running stale app.js after the server is rebuilt/restarted.
var buildId = Guid.NewGuid().ToString("N")[..12];
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", runtime = "microsoft-agent-framework" }));
app.MapGet("/api/version", () => Results.Ok(new { version = buildId }));
app.MapGet("/api/config", (HarnessConfiguration config) => Results.Ok(config.Load()));
app.MapPut("/api/config", async (HttpRequest request, HarnessConfiguration config) =>
{
    var update = await JsonNode.ParseAsync(request.Body) ?? throw new BadHttpRequestException("Invalid JSON");
    var path = Path.Combine(config.Workspace, "open-agent-qa.json");
    var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var target = root["open-agent-qa"]?.AsObject() ?? root;
    foreach (var pair in update.AsObject()) if (pair.Key != "mcp") target[pair.Key] = pair.Value?.DeepClone();
    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/mcp-config", (HarnessConfiguration config) =>
{
    var path = config.ActiveAgentMcpPath();
    return File.Exists(path) ? Results.File(path, "application/json") : Results.Ok(new { mcpServers = new { } });
});
app.MapPut("/api/mcp-config", async (HttpRequest request, HarnessConfiguration config) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync();
    JsonDocument.Parse(body);
    var path = config.ActiveAgentMcpPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, body);
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/agents", (HarnessConfiguration config) => Results.Ok(config.ListAgents()));
app.MapPost("/api/agents/active", (SetAgentRequest request, HarnessConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(request.Agent)) return Results.BadRequest(new { error = "agent is required" });
    var available = config.ListAgents().Agents.Select(agent => agent.Name);
    if (!available.Contains(request.Agent, StringComparer.OrdinalIgnoreCase)) return Results.NotFound(new { error = $"Unknown agent: {request.Agent}" });
    config.SetActiveAgent(request.Agent);
    return Results.Ok(new { ok = true, active = request.Agent });
});
app.MapGet("/api/chat/info", (HarnessConfiguration config) =>
{
    var loaded = config.Load();
    var agentPath = loaded.AgentConfig.AgentMd is null ? null : Path.GetFullPath(Path.Combine(config.Workspace, loaded.AgentConfig.AgentMd));
    var skillsPath = loaded.AgentConfig.SkillsDir is null ? null : Path.GetFullPath(Path.Combine(config.Workspace, loaded.AgentConfig.SkillsDir));
    return Results.Ok(new { agentMd = loaded.AgentConfig.AgentMd, agentMdLength = agentPath is not null && File.Exists(agentPath) ? File.ReadAllText(agentPath).Length : 0, skills = skillsPath is not null ? config.ListSkillNames(skillsPath) : [], skillsDir = loaded.AgentConfig.SkillsDir, mcpServers = loaded.McpServers.Select(server => new { name = server.Name, command = server.Command }), model = loaded.Model });
});
app.MapGet("/api/models", (HarnessConfiguration config) => Results.Ok(new { models = new[] { new { id = config.Load().Model, name = config.Load().Model } } }));
app.MapGet("/api/mcp/tools", async (AgentRuntime runtime, CancellationToken token) => Results.Ok(await runtime.InspectToolsAsync(token)));
app.MapGet("/api/tests", (RunCoordinator runs) => Results.Ok(runs.DiscoverTests()));
app.MapPost("/api/tests/run", (RunRequest request, RunCoordinator runs) => Results.Ok(new { jobId = runs.Start(request).Id }));
app.MapPost("/api/setup/run", async (HttpContext context, RunCoordinator runs) =>
{
    var token = context.RequestAborted;
    context.Response.ContentType = "text/plain; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff"; // don't let the browser buffer for MIME sniffing
    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

    // Immediate feedback so "on click" is never blank, even before the script's first line.
    await context.Response.WriteAsync("▶ Launching setup script…\n", token);
    await context.Response.Body.FlushAsync(token);

    // Stream each line as the script produces it so the browser shows live progress.
    var result = await runs.RunSetupScriptAsync(async line =>
    {
        await context.Response.WriteAsync(line + "\n", token);
        await context.Response.Body.FlushAsync(token);
    }, token);
    // Terminal sentinel the UI keys off of (status can't change once streaming has begun).
    await context.Response.WriteAsync(result.Ok ? $"\n[DONE] Clean instance ready (exit {result.ExitCode}).\n" : $"\n[ERROR] {result.Error}\n", token);
    await context.Response.Body.FlushAsync(token);
});
app.MapGet("/api/tests/run/{id}", (string id, RunCoordinator runs) => runs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "Run not found" }));
app.MapPost("/api/tests/run/{id}/cancel", (string id, RunCoordinator runs) => runs.Cancel(id) ? Results.Ok() : Results.NotFound(new { error = "Active run not found" }));
app.MapGet("/api/runs", (RunCoordinator runs) => Results.Ok(runs.ListRuns()));
app.MapGet("/api/runs/{id}/artifacts/{artifact}", (string id, string artifact, RunCoordinator runs) => runs.GetRunArtifact(id, artifact) ?? Results.NotFound(new { error = "Run not found" }));
app.MapGet("/api/runs/{id}/prompts/{**path}", (string id, string path, RunCoordinator runs) => runs.GetPromptArtifact(id, path) ?? Results.NotFound(new { error = "Prompt artifact not found" }));
app.MapGet("/api/runs/{id}/download", (string id, RunCoordinator runs) => runs.DownloadRun(id) ?? Results.NotFound(new { error = "Run not found" }));
app.MapPost("/api/runs/{id}/open", (string id, RunCoordinator runs) => runs.OpenRun(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Run not found" }));
app.MapDelete("/api/runs/{id}", (string id, RunCoordinator runs) => runs.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Run not found" }));
app.MapGet("/api/reports", (HarnessConfiguration config) => Results.Ok(ListJsonFiles(Path.Combine(config.Workspace, ".harness", "reports"))));
app.MapGet("/api/reports/{name}", (string name, HarnessConfiguration config) => ReadJsonFile(Path.Combine(config.Workspace, ".harness", "reports"), name));
app.MapGet("/api/logs", (HarnessConfiguration config) => Results.Ok(ReadAllJson(Path.Combine(config.Workspace, ".harness", "logs"))));
app.MapGet("/api/issues", (HarnessConfiguration config) => Results.Ok(ReadAllJson(Path.Combine(config.Workspace, ".harness", "issues"))));
app.MapPost("/api/chat", async (ChatRequest request, AgentRuntime runtime, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required" });
    try { return Results.Ok(await runtime.RunAsync(request.Message, request.Conversation, token)); }
    catch (Exception error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapHub<RunHub>("/hubs/runs");
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(uiDirectory) });

app.Run();
return 0;

static IReadOnlyList<string> ListJsonFiles(string directory) => Directory.Exists(directory)
    ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileName).Where(name => name is not null).Cast<string>().OrderDescending().ToList()
    : [];

static IResult ReadJsonFile(string directory, string name)
{
    if (Path.GetFileName(name) != name) return Results.BadRequest(new { error = "Invalid file name" });
    var path = Path.Combine(directory, name);
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
}

static IReadOnlyList<JsonElement> ReadAllJson(string directory) => Directory.Exists(directory)
    ? Directory.EnumerateFiles(directory, "*.json").Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()).ToList()
    : [];

static string FindWorkspace(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "open-agent-qa.json"))) return directory.FullName;
    throw new DirectoryNotFoundException("Could not find open-agent-qa.json above the application directory.");
}
