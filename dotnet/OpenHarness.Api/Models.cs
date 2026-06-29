namespace OpenHarness.Api;

public sealed record HarnessConfig(
    string Provider,
    string Model,
    string TestDir,
    double Temperature,
    int MaxSteps,
    int AgentTimeoutMs,
    int Parallel,
    AgentConfig AgentConfig,
    IReadOnlyList<McpServerConfig> McpServers,
    string? SetupScript = null,
    string ActiveAgent = "",
    string AgentsDir = "./Agents");

public sealed record AgentConfig(string? AgentMd, string? SkillsDir);

/// <summary>One agent under the Agents folder, for the picker. Paths are absolute (or null).</summary>
public sealed record AgentInfo(string Name, string? Description, string? TestDir, int SkillCount, int McpServerCount, bool HasSetupScript);
public sealed record AgentList(string Active, IReadOnlyList<AgentInfo> Agents);

public sealed record McpServerConfig(string Name, string? Type, string? Url, string? Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, int TimeoutMs);

public sealed record TestCase(
    string Path,
    string Name,
    string Prompt,
    string? Expected = null,
    string? Asserts = null,
    string? ModelOverride = null,
    double? TemperatureOverride = null);

public sealed record Issue(
    string Id,
    string Test,
    string Status,
    string Severity,
    string Summary,
    string Trace,
    string Created,
    string? Resolved);
public sealed record ToolCallTrace(string Tool, object? Input, object? Output, DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null, long? DurationMs = null);
public sealed record TokenUsage(long Prompt, long Completion, long Total, long Cached = 0, double Cost = 0);
/// <summary>One assistant turn in the reconstructed conversation: the model's text for that
/// step, the tool calls it made (with results), and that single LLM call's token/cost usage.</summary>
public sealed record ConversationTurn(string Role, string? Text, IReadOnlyList<ToolCallTrace> ToolCalls, TokenUsage? Usage);
public sealed record TestResult(TestCase Test, string Response, long DurationMs, IReadOnlyList<ToolCallTrace> Trace, TokenUsage? TokenUsage, string? Error, DateTimeOffset StartedAt = default, DateTimeOffset CompletedAt = default, IReadOnlyList<ConversationTurn>? Conversation = null);
public sealed record RunReport(DateTimeOffset StartedAt, long DurationMs, int Total, IReadOnlyList<string> Logs, IReadOnlyList<TestResult> Results, HarnessConfig Config);

public sealed record ChatRequest(string Message, IReadOnlyList<ChatTurn>? Conversation);
public sealed record ChatTurn(string Role, string Content);
public sealed record ChatResult(string Response, IReadOnlyList<ToolCallTrace> ToolCalls, TokenUsage? TokenUsage, IReadOnlyList<ConversationTurn>? Conversation = null);
public sealed record RunRequest(IReadOnlyList<string>? Files, int? Parallel, string? Model);
public sealed record SetAgentRequest(string Agent);

public sealed class RunJob
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int Total { get; init; }
    public int Completed { get; set; }
    public string Status { get; set; } = "running";
    public List<RunJobTest> Tests { get; init; } = [];
    public RunReport? Report { get; set; }
    public string? Error { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Directory { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource Cancellation { get; } = new();
}

public sealed class RunJobTest
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string Status { get; set; } = "queued";
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public required string ArtifactPath { get; init; }
}

public sealed record RunListItem(string Id, string Status, DateTimeOffset StartedAt, int Total, int Completed, string? Error);

public sealed record SetupScriptResult(bool Ok, int? ExitCode, string Stdout, string Stderr, string? Error);
