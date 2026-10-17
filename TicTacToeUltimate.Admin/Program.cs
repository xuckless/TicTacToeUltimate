using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TicTacToeUltimate.Admin;
using TicTacToeUltimate.Admin.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Production: same origin (the admin worker proxies /api/admin to the game server). Development: the local game server.
var api = builder.Configuration["Api:BaseUrl"] is { Length: > 0 } b ? new Uri(b) : new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(_ => new AdminApi(new HttpClient { BaseAddress = api }, builder.Configuration["Api:DevEmail"]));
builder.Services.AddScoped<Theme>();

await builder.Build().RunAsync();
