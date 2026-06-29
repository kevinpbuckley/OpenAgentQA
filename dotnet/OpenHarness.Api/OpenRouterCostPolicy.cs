using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenHarness.Api;

/// <summary>One OpenRouter HTTP completion call's usage: tokens + USD cost.</summary>
internal sealed record LlmCallUsage(long Prompt, long Completion, long Total, long Cached, double Cost);

/// <summary>
/// OpenRouter only reports usage details (including <c>cost</c>) when the request opts in with
/// <c>usage.include = true</c>, so we inject that flag and then read the usage block off each raw
/// response. Each completion in the tool loop is recorded separately (in call order) so the report
/// can show per-interaction tokens/cost, not just a run total.
///
/// Cost note: under BYOK (bring-your-own-key) OpenRouter does not bill, so <c>usage.cost</c> is 0 —
/// the real spend is in <c>usage.cost_details.upstream_inference_cost</c>. We use <c>cost</c> when
/// non-zero and fall back to the upstream cost otherwise.
/// </summary>
internal sealed class OpenRouterCostPolicy : PipelinePolicy
{
    private static readonly AsyncLocal<ConcurrentQueue<LlmCallUsage>?> Current = new();

    /// <summary>Scope per-call usage capture to the awaited work inside the using block.</summary>
    public static IDisposable Accumulate(ConcurrentQueue<LlmCallUsage> sink)
    {
        Current.Value = sink;
        return new Scope();
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectUsageInclude(message);
        ProcessNext(message, pipeline, currentIndex);
        CaptureUsage(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectUsageInclude(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
        CaptureUsage(message);
    }

    private static void InjectUsageInclude(PipelineMessage message)
    {
        if (Current.Value is null || message.Request?.Content is null) return;
        try
        {
            using var buffer = new MemoryStream();
            message.Request.Content.WriteTo(buffer);
            if (buffer.Length == 0) return;
            if (JsonNode.Parse(buffer.ToArray()) is not JsonObject root) return;
            if (root["usage"] is not JsonObject usage)
            {
                usage = new JsonObject();
                root["usage"] = usage;
            }
            usage["include"] = true;
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(root.ToJsonString()));
        }
        catch
        {
            // Not a JSON chat-completions body — leave it untouched.
        }
    }

    private static void CaptureUsage(PipelineMessage message)
    {
        var sink = Current.Value;
        if (sink is null) return;
        try
        {
            var content = message.Response?.Content;
            if (content is null) return;
            if (JsonNode.Parse(content.ToString())?["usage"] is not JsonNode usage) return;

            var cost = ReadDouble(usage["cost"]);
            if (cost <= 0) cost = ReadDouble(usage["cost_details"]?["upstream_inference_cost"]); // BYOK
            sink.Enqueue(new LlmCallUsage(
                ReadLong(usage["prompt_tokens"]),
                ReadLong(usage["completion_tokens"]),
                ReadLong(usage["total_tokens"]),
                ReadLong(usage["prompt_tokens_details"]?["cached_tokens"]),
                cost));
        }
        catch
        {
            // Streaming or non-JSON response — nothing to read.
        }
    }

    private static long ReadLong(JsonNode? node)
    {
        try { return node is null ? 0 : (long)Math.Round(node.GetValue<double>()); }
        catch { return 0; }
    }

    private static double ReadDouble(JsonNode? node)
    {
        try { return node?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
