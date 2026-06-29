using Microsoft.AspNetCore.SignalR;

namespace OpenHarness.Api;

public sealed class RunHub : Hub
{
    public Task JoinRun(string jobId) => Groups.AddToGroupAsync(Context.ConnectionId, jobId);
}
