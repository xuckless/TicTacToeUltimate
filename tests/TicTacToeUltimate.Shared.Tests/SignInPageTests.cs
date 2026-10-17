using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Shared.Fakes;
using TicTacToeUltimate.Shared.Pages;
using TicTacToeUltimate.Shared.Services;

namespace TicTacToeUltimate.Shared.Tests;

public sealed class SignInPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SignInPageTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddTttSharedUi();
        _ctx.Services.AddTttFakeStores();
        var session = (FakeSessionStore)_ctx.Services.GetRequiredService<ISessionStore>();
        session.Set(MockupFixtures.GuestSession with
        {
            Config = new AppConfigDto(1, false, true, "https://supabase.test", "anon", [], [], [], 0, new AdConfigDto(2, true), ""),
        });
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Landing_has_log_in_button_and_quiet_create_link()
    {
        var cut = _ctx.Render<SignInPage>();
        Assert.Equal("Log in", cut.Find("[data-testid=login]").TextContent.Trim());
        Assert.Equal("Create account", cut.Find("[data-testid=create-account]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=email-submit]"));
    }

    [Fact]
    public void Log_in_and_create_open_the_same_email_step_with_their_own_wording()
    {
        var cut = _ctx.Render<SignInPage>();
        cut.Find("[data-testid=login]").Click();
        Assert.Contains("Welcome back", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid=email-submit]"));

        var create = _ctx.Render<SignInPage>();
        create.Find("[data-testid=create-account]").Click();
        Assert.Contains("Create your account", create.Markup, StringComparison.Ordinal);
        Assert.Single(create.FindAll("[data-testid=email-submit]"));
    }

    [Fact]
    public void Mode_query_skips_the_landing()
    {
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("signin?mode=create");
        var cut = _ctx.Render<SignInPage>();
        Assert.Contains("Create your account", cut.Markup, StringComparison.Ordinal);
    }
}
