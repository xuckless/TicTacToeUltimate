using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TicTacToeUltimate.Server.IntegrationTests;

public class SmokeTests
{
    [Fact]
    public async Task HealthReturns200()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
