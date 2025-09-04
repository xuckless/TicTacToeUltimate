using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TicTacToeUltimate.Shared.Services;
using TicTacToeUltimate.Web.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Add device-specific services used by the TicTacToeUltimate.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

await builder.Build().RunAsync();