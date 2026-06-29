using System.ClientModel.Primitives;

namespace OpenHarness.Api;

/// <summary>
/// Adds OpenRouter app-attribution headers to every request so usage shows up under this app on
/// OpenRouter's rankings/analytics (https://openrouter.ai/docs/app-attribution). <c>HTTP-Referer</c>
/// is the required primary identifier; <c>X-OpenRouter-Title</c> sets the display name. Both default
/// to OpenAgentQA and can be overridden with the <c>OPENROUTER_HTTP_REFERER</c> / <c>OPENROUTER_X_TITLE</c>
/// env vars.
/// </summary>
internal sealed class OpenRouterAttributionPolicy : PipelinePolicy
{
    public const string DefaultReferer = "https://github.com/kevinpbuckley/OpenAgentQA";
    public const string DefaultTitle = "OpenAgentQA";

    private readonly string _referer;
    private readonly string _title;

    public OpenRouterAttributionPolicy()
    {
        _referer = NonEmpty(Environment.GetEnvironmentVariable("OPENROUTER_HTTP_REFERER")) ?? DefaultReferer;
        _title = NonEmpty(Environment.GetEnvironmentVariable("OPENROUTER_X_TITLE")) ?? DefaultTitle;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineMessage message)
    {
        if (message.Request is null) return;
        message.Request.Headers.Set("HTTP-Referer", _referer);
        message.Request.Headers.Set("X-OpenRouter-Title", _title);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
