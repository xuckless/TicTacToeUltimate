using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using TicTacToeUltimate.Shared.Services;
using TicTacToeUltimate.Web.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Shared/device services
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// SignalR client (browser connects to same-origin /hubs/test)
builder.Services.AddSingleton(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HubConnectionBuilder()
        .WithUrl(nav.ToAbsoluteUri("/hubs/test"))
        .WithAutomaticReconnect()
        .Build();
});

await builder.Build().RunAsync();