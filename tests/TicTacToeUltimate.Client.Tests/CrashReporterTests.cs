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

    // Stand-in for System.Runtime.InteropServices.JavaScript.JSException (browser only); matched by type name.
    private sealed class JSException(string message) : Exception(message);

    private sealed class AbortingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new JSException("AbortError: Fetch is aborted");
    }

    [Fact]
    public async Task Opaque_cross_origin_script_errors_and_browser_aborts_are_not_reported()   // GitHub issues #5 and #6
    {
        var handler = new Handler { Online = true };
        var trail = new DiagnosticsTrail();
        var api = new TttApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test/") }, new TokenStore(new InMemorySecureStore()), trail);
        var reporter = new CrashReporter(api, trail, new RingLoggerProvider(), new TestPlatform(), new InMemoryKeyValueStore());

        await reporter.ReportCrashAsync("js:Error", "Script error.", "");
        await reporter.ReportCrashAsync("js:UnhandledRejection", "AbortError: Fetch is aborted", "");
        await reporter.ReportCrashAsync(new JSException("AbortError: Fetch is aborted"));
        Assert.Empty(handler.Bodies);

        await reporter.ReportCrashAsync("js:TypeError", "x is undefined", "app.js:1:2");
        await reporter.ReportCrashAsync("js:TypeError", "x is undefined", "app.js:1:2");   // same JS error within a minute: once
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task A_fetch_the_browser_aborted_is_a_network_error()   // GitHub issue #6
    {
        var api = new TttApiClient(new HttpClient(new AbortingHandler()) { BaseAddress = new Uri("http://test/") }, new TokenStore(new InMemorySecureStore()), new DiagnosticsTrail());
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetConfigAsync());
        Assert.True(BrowserAbort.Is(e));
    }
}
