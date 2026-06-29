using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace OpenHarness.Api;

/// <summary>
/// The single agent entrypoint. Agent Framework owns the model/tool loop while
/// the official MCP SDK supplies the tool implementations.
/// </summary>
public sealed class AgentRuntime(HarnessConfiguration configuration, ILogger<AgentRuntime> logger)
{
    public async Task<ChatResult> RunAsync(string message, IReadOnlyList<ChatTurn>? conversation, CancellationToken cancellationToken)
        => await RunAsync(message, conversation, null, null, cancellationToken);

    public async Task<ChatResult> RunAsync(string message, IReadOnlyList<ChatTurn>? conversation, string? modelOverride, double? temperatureOverride, CancellationToken cancellationToken)
    {
        var config = configuration.Load();
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? throw new InvalidOperationException("OPENROUTER_API_KEY environment variable is required");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.AgentTimeoutMs);
        var traces = new List<ToolCallTrace>();
        var connections = new List<McpConnection>();
        try
        {
            // Record exactly which MCP tools were in play (server, name, description) so the
            // analyzer can judge tool descriptions/coverage without re-deriving them.
            var availableTools = new List<ToolInfo>();
            foreach (var server in config.McpServers)
            {
                var connection = await McpConnection.ConnectAsync(server, logger, timeout.Token);
                connections.Add(connection);
                foreach (var tool in connection.Tools)
                    availableTools.Add(new ToolInfo(server.Name, tool.Name, tool.Description));
            }

            // Wrap each tool so we record when each call started/ended — the report view
            // renders per-call duration and the gap between calls.
            var callTimings = new ConcurrentQueue<ToolCallTiming>();
            var tools = connections
                .SelectMany(connection => connection.Tools)
                .Select(tool => (AITool)new TimedFunction(tool, callTimings))
                .ToList();
            var endpoint = new Uri("https://openrouter.ai/api/v1");
            var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };
            clientOptions.AddPolicy(new OpenRouterCostPolicy(), PipelinePosition.PerCall);
            clientOptions.AddPolicy(new OpenRouterAttributionPolicy(), PipelinePosition.PerCall);
            var openAi = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            using IChatClient chatClient = openAi.GetChatClient(config.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();

            var agentOptions = new ChatClientAgentOptions
            {
                Name = "openharness-agent",
                Description = "OpenHarness QA agent",
                ChatOptions = new ChatOptions { Instructions = configuration.LoadInstructions(config), Tools = tools },
            };
            // MAF native Agent Skills: advertise the active agent's skills (name + description) and let
            // the model pull each SKILL.md on demand via the load_skill tool (progressive disclosure).
            // AgentSkillsProvider is an evaluation/experimental MAF API (MAAI001) — opted into here.
            var skillsDir = configuration.ActiveSkillsDir(config);
            if (skillsDir is not null && configuration.DiscoverSkillFiles(skillsDir).Count > 0)
            {
#pragma warning disable MAAI001
                agentOptions.AIContextProviders = [new AgentSkillsProvider(skillsDir)];
#pragma warning restore MAAI001
            }

            var agent = new ChatClientAgent(chatClient, agentOptions, loggerFactory: null, services: null);

            var messages = new List<ChatMessage>();
            foreach (var turn in conversation ?? [])
            {
                messages.Add(new ChatMessage(turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User, turn.Content));
            }
            messages.Add(new ChatMessage(ChatRole.User, message));

            // Per-test frontmatter (model/temperature) overrides the global config.
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                ModelId = modelOverride ?? config.Model,
                Temperature = (float)(temperatureOverride ?? config.Temperature),
            });

            var callUsages = new ConcurrentQueue<LlmCallUsage>();
            AgentResponse response;
            using (OpenRouterCostPolicy.Accumulate(callUsages))
            {
                response = await agent.RunAsync(messages, null, runOptions, timeout.Token);
            }
            var usages = callUsages.ToArray();
            var conversationTurns = BuildConversation(response, traces, [.. callTimings], usages);
            var totalCost = usages.Sum(usage => usage.Cost);

            // What was advertised vs what the agent actually loaded — the key skill signal.
            var advertisedSkills = skillsDir is not null ? configuration.AdvertisedSkills(skillsDir) : [];
            var loadedSkills = ExtractLoadedSkills(traces, advertisedSkills);

            return new ChatResult(response.Text ?? string.Empty, traces, ExtractUsage(response, totalCost),
                conversationTurns, availableTools, advertisedSkills, loadedSkills);
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Reconstruct the conversation from the run's message history. UseFunctionInvocation records
    /// the back-and-forth as message content; we walk it in order to recover both the flat tool
    /// transcript (<paramref name="traces"/>, paired call→result by CallId, with per-call timings
    /// from <see cref="TimedFunction"/> matched in invocation order) and the per-turn conversation:
    /// each assistant message is one LLM call, so its text + tool calls are grouped together and the
    /// k-th assistant turn is given the k-th captured <see cref="LlmCallUsage"/> (tokens + cost).
    /// </summary>
    private static List<ConversationTurn> BuildConversation(
        AgentResponse response, List<ToolCallTrace> traces, IReadOnlyList<ToolCallTiming> timings, IReadOnlyList<LlmCallUsage> usages)
    {
        var results = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
                if (content is FunctionResultContent result && result.CallId is not null)
                    results[result.CallId] = result.Result;

        // Flat trace in message (= call) order.
        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
                if (content is FunctionCallContent call)
                    traces.Add(new ToolCallTrace(
                        call.Name,
                        call.Arguments,
                        call.CallId is not null && results.TryGetValue(call.CallId, out var output) ? output : null));

        // Match per-call timings onto the flat trace, in order. Only MCP tools are timing-wrapped
        // (TimedFunction), so a trace whose name doesn't match the next timing — e.g. the skills
        // provider's load_skill — is left untimed without consuming a timing.
        var ti = 0;
        for (var i = 0; i < traces.Count && ti < timings.Count; i++)
        {
            if (timings[ti].Name != traces[i].Tool) continue;
            var timing = timings[ti++];
            traces[i] = traces[i] with { StartedAt = timing.StartedAt, EndedAt = timing.EndedAt, DurationMs = timing.DurationMs };
        }

        // Group the (now timing-stamped) traces back into per-assistant-turn slices.
        var turns = new List<ConversationTurn>();
        var traceIndex = 0;
        var assistantIndex = 0;
        foreach (var message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant) continue;
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(part => part.Text));
            var callCount = message.Contents.OfType<FunctionCallContent>().Count();
            var calls = traces.GetRange(traceIndex, callCount);
            traceIndex += callCount;
            var usage = assistantIndex < usages.Count ? ToTokenUsage(usages[assistantIndex]) : null;
            assistantIndex++;
            if (string.IsNullOrWhiteSpace(text) && calls.Count == 0) continue; // empty turn — skip in the view
            turns.Add(new ConversationTurn("assistant", string.IsNullOrWhiteSpace(text) ? null : text, calls, usage));
        }
        return turns;
    }

    private static TokenUsage ToTokenUsage(LlmCallUsage usage) =>
        new(usage.Prompt, usage.Completion, usage.Total, usage.Cached, usage.Cost);

    /// <summary>
    /// Which advertised skills the agent actually loaded, from the <c>load_skill</c> tool calls in the
    /// trace (the provider's progressive-disclosure tool). A skill counts as loaded when its name
    /// appears in a load_skill call's arguments. Deterministic — no interpretation.
    /// </summary>
    private static IReadOnlyList<string> ExtractLoadedSkills(IReadOnlyList<ToolCallTrace> traces, IReadOnlyList<SkillInfo> advertised)
    {
        if (advertised.Count == 0) return [];
        var loaded = new List<string>();
        foreach (var trace in traces)
        {
            if (!trace.Tool.Contains("load_skill", StringComparison.OrdinalIgnoreCase)) continue;
            var input = System.Text.Json.JsonSerializer.Serialize(trace.Input);
            foreach (var skill in advertised)
                if (!loaded.Contains(skill.Name) && input.Contains(skill.Name, StringComparison.OrdinalIgnoreCase))
                    loaded.Add(skill.Name);
        }
        return loaded;
    }

    private static TokenUsage? ExtractUsage(AgentResponse response, double cost)
    {
        var usage = response.Usage;
        if (usage is null) return cost > 0 ? new TokenUsage(0, 0, 0, 0, cost) : null;
        return new TokenUsage(
            usage.InputTokenCount ?? 0,
            usage.OutputTokenCount ?? 0,
            usage.TotalTokenCount ?? 0,
            usage.CachedInputTokenCount ?? 0,
            cost);
    }

    public async Task<IReadOnlyList<object>> InspectToolsAsync(CancellationToken cancellationToken)
    {
        var result = new List<object>();
        foreach (var server in configuration.Load().McpServers)
        {
            try
            {
                await using var connection = await McpConnection.ConnectAsync(server, logger, cancellationToken);
                result.Add(new { server = server.Name, tools = connection.Tools.Select(tool => new { name = tool.Name, description = tool.Description }) });
            }
            catch (Exception error)
            {
                result.Add(new { server = server.Name, tools = Array.Empty<object>(), error = error.Message });
            }
        }
        return result;
    }
}

/// <summary>When the tool started/ended and how long it ran — one per invocation.</summary>
internal sealed record ToolCallTiming(string Name, DateTimeOffset StartedAt, DateTimeOffset EndedAt, long DurationMs);

/// <summary>
/// Wraps an MCP tool so each invocation's start/end/duration is recorded into a shared queue,
/// without altering the tool's name, schema, or result. Function invocation is sequential, so the
/// queue preserves call order and <c>ExtractTraces</c> can zip the timings back onto the transcript.
/// </summary>
internal sealed class TimedFunction(AIFunction inner, ConcurrentQueue<ToolCallTiming> sink) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            sink.Enqueue(new ToolCallTiming(Name, started, DateTimeOffset.UtcNow, stopwatch.ElapsedMilliseconds));
        }
    }
}

internal sealed class McpConnection(McpClient client, IReadOnlyList<McpClientTool> tools) : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConnectionGates = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<McpClientTool> Tools { get; } = tools;

    public static async Task<McpConnection> ConnectAsync(McpServerConfig server, ILogger logger, CancellationToken cancellationToken)
    {
        var gate = ConnectionGates.GetOrAdd(server.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
        IClientTransport transport;
        if (string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(server.Url)) throw new InvalidOperationException($"MCP server '{server.Name}' has no url.");
            var httpClient = new HttpClient(new ContentLengthHandler { InnerHandler = new SocketsHttpHandler() });
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url),
                Name = server.Name,
                // Unreal exposes Streamable HTTP directly and rejects the SDK's
                // AutoDetect SSE probe with 405.
                TransportMode = HttpTransportMode.StreamableHttp,
            }, httpClient, ownsHttpClient: true);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(server.Command)) throw new InvalidOperationException($"MCP server '{server.Name}' has no command.");
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in server.Env) environment[pair.Key] = pair.Value;
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = [.. server.Args],
                Name = server.Name,
                InheritEnvironmentVariables = true,
                EnvironmentVariables = environment,
            });
        }

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return new McpConnection(client, [.. tools]);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>Epic's HTTP server requires Content-Length instead of chunked JSON bodies.</summary>
internal sealed class ContentLengthHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is { Headers.ContentLength: null } content)
        {
            var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
            var replacement = new ByteArrayContent(bytes);
            foreach (var header in content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            replacement.Headers.ContentLength = bytes.Length;
            request.Content = replacement;
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
