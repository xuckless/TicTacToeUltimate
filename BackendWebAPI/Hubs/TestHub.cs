using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace BackendWebAPI.Hubs
{
    public class TestHub : Hub
    {
        public async Task SendMessage(string message) =>
            await Clients.All.SendAsync("ReceiveMessage", message);
    }
}