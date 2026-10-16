using System.Net;
using TicTacToeUltimate.Client.Abstractions;
using TicTacToeUltimate.Client.Diagnostics;
using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Tests;

public sealed class CrashReporterTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public bool Online { get; set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Online) throw new HttpRequestException("offline");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"fingerprint":"x"}""", System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestPlatform : IPlatformInfo
    {
        public ClientPlatform Platform => ClientPlatform.Web;
        public bool IsTouch => false;
        public bool SupportsHaptics => false;
        public string AppVersion => "9.9";
    }

    [Fact]
    public async Task Crashes_wait_offline_then_send_once_with_the_trail()
    {
        var handler = new Handler();
        var trail = new DiagnosticsTrail();
        var tokens = new TokenStore(new InMemorySecureStore());
        var api = new TttApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test/") }, tokens, trail);
        var reporter = new CrashReporter(api, trail, new RingLoggerProvider(), new TestPlatform(), new InMemoryKeyValueStore());
        trail.Add("nav", "/play");
        trail.Add("tap", "button:play-again");

        await reporter.ReportCrashAsync(new InvalidOperationException("boom"));
        await reporter.ReportCrashAsync(new InvalidOperationException("boom"));   // same error within a minute: once
        Assert.Empty(handler.Bodies);

        handler.Online = true;
        await reporter.FlushAsync();
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("button:play-again", body, StringComparison.Ordinal);
        Assert.Contains("\"appVersion\":\"9.9\"", body, StringComparison.Ordinal);
        await reporter.FlushAsync();
        Assert.Single(handler.Bodies);
    }
}
