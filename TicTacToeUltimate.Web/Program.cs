using Microsoft.AspNetCore.SignalR.Client;
using TicTacToeUltimate.Web.Components;
using TicTacToeUltimate.Shared.Services;
using TicTacToeUltimate.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Shared/device services
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// SignalR client for server-side features (points to BackendWebAPI)
var backendBase = builder.Configuration["BackendBaseUrl"] ?? "https://localhost:7086";
builder.Services.AddSingleton(sp =>
    new HubConnectionBuilder()
        .WithUrl($"{backendBase}/hubs/test")
        .WithAutomaticReconnect()
        .Build());

var app = builder.Build();

// Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(TicTacToeUltimate.Shared._Imports).Assembly,
        typeof(TicTacToeUltimate.Web.Client._Imports).Assembly);

app.Run();