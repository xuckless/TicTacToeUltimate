using Microsoft.AspNetCore.SignalR;

namespace BackendWebAPI.Hubs
{
    public class TestHub : Hub
    {
        // clients call: InvokeAsync("SendMessage", "<text>")
        [HubMethodName("SendMessage")]
        public Task SendMessage(string message)
            => Clients.All.SendAsync("ReceiveMessage", message);
    }
}