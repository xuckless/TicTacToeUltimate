using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicTacToeUltimate.Client;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.Stores;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Shared;
using TicTacToeUltimate.Web.Client;

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
// Host overrides (last registration wins). IAppLifecycle stays the Shared JsLifecycle registered by AddTttSharedUi.
builder.Services.Replace(ServiceDescriptor.Singleton<IPlatformInfo, WebPlatformInfo>());
builder.Services.Replace(ServiceDescriptor.Singleton<IAdProvider, StubAdProvider>());
builder.Services.AddTttClientFallbacks();

var host = builder.Build();

// Dev-only "?as={username}": sign this tab in as a fixture user without touching localStorage (two tabs can play).
if (options.IsDevelopment)
{
    var uri = new Uri(host.Services.GetRequiredService<NavigationManager>().Uri);
    var asUser = System.Web.HttpUtility.ParseQueryString(uri.Query)["as"];
    if (!string.IsNullOrWhiteSpace(asUser)) host.Services.GetRequiredService<SessionStore>().UseDevImpersonation(asUser);
}

await host.RunAsync();
