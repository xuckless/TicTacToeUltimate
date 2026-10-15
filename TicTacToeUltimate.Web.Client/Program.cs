using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TicTacToeUltimate.Client;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Shared;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var options = new ClientOptions
{
    BackendBaseUrl = new Uri(builder.Configuration["Backend:BaseUrl"] ?? "http://localhost:5091"),
    WebBaseUrl = new Uri(builder.Configuration["Web:BaseUrl"] ?? builder.HostEnvironment.BaseAddress),
    Platform = ClientPlatform.Web,
    IsDevelopment = builder.HostEnvironment.IsDevelopment(),
};

builder.Services.AddTttClient(options);
builder.Services.AddTttSharedUi();

await builder.Build().RunAsync();
