using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ReleaseManager.Api.Hubs;

[Authorize]
public sealed class ReleaseHub : Hub
{
    public Task JoinTask(string taskId) => Groups.AddToGroupAsync(Context.ConnectionId, taskId);
    public Task LeaveTask(string taskId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, taskId);
}
