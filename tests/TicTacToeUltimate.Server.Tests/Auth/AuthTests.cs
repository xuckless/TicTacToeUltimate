using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TicTacToeUltimate.Contracts;
using TicTacToeUltimate.Server.Abstractions;
using TicTacToeUltimate.Server.Abstractions.Persistence;
using TicTacToeUltimate.Server.Auth;
using TicTacToeUltimate.Server.Platform;
using TicTacToeUltimate.Server.Tests.Persistence;

namespace TicTacToeUltimate.Server.Tests.Auth;

public class AuthTests
{
    private static string N(string p) => p + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void ProductionWithDevEnabledThrowsAtStartup()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ttt:Auth:Dev:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddTttPlatform(cfg, new FakeEnv("Production")));
        var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ttt:Auth:Dev:Enabled"] = "false" }).Build();
        new ServiceCollection().AddTttPlatform(off, new FakeEnv("Production"));
    }

    [Fact]
    public async Task DevTokenValidatesWithDevScheme()
    {
        using var p = new TestPlatform();
        var accounts = p.Get<AccountService>();
        var res = await accounts.DevTokenAsync(new DevTokenRequestDto(null, N("dev_")), default);
        Assert.True(res.Ok);
        var token = res.Value!;
        Assert.Equal(AccountKind.Account, token.Me.Kind);
        Assert.False(token.Me.NeedsUsername);
        var issuer = p.Get<DevTokenIssuer>();
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, issuer.ValidationParameters());
        Assert.True(validated.IsValid);
        Assert.Equal(token.Me.UserId.ToString(), validated.Claims[TttClaims.Sub]);
        Assert.Equal("account", validated.Claims[TttClaims.Kind]);
        Assert.Equal(TestPlatform.Start.AddDays(30).ToUnixTimeMilliseconds(), token.ExpiresAtMs);

        var again = await accounts.DevTokenAsync(new DevTokenRequestDto(token.Me.UserId, null), default);
        Assert.Equal(token.Me.UserId, again.Value!.Me.UserId);
        Assert.Equal(ErrorCodes.Validation, (await accounts.DevTokenAsync(new DevTokenRequestDto(token.Me.UserId, "both_set"), default)).Error);
        Assert.Equal(ErrorCodes.UsernameTaken, (await accounts.DevTokenAsync(new DevTokenRequestDto(null, token.Me.Username), default)).Error);
    }

    [Fact]
    public async Task GuestTokenIsStablePerDevice_UpgradeKeepsId()
    {
        using var p = new TestPlatform();
        var accounts = p.Get<AccountService>();
        var device = Guid.NewGuid().ToString();
        var g1 = (await accounts.GuestTokenAsync(new GuestTokenRequestDto(device), default)).Value!;
        var g2 = (await accounts.GuestTokenAsync(new GuestTokenRequestDto(device), default)).Value!;
        Assert.Equal(g1.Me.UserId, g2.Me.UserId);
        Assert.Equal(AccountKind.Guest, g1.Me.Kind);
        Assert.Matches("^Guest#[0-9]{4}$", g1.Me.DisplayName);
        Assert.Empty(g1.Me.Ratings);

        Assert.Equal(ErrorCodes.TosRequired, (await accounts.SetUsernameAsync(g1.Me.UserId, new SetUsernameDto(N("up_"), false), default)).Error);
        var name = N("up_");
        var up = await accounts.SetUsernameAsync(g1.Me.UserId, new SetUsernameDto(name.ToUpperInvariant(), true), default);
        Assert.True(up.Ok);
        Assert.Equal(g1.Me.UserId, up.Value!.UserId);
        Assert.Equal(AccountKind.Account, up.Value.Kind);
        Assert.Equal(name, up.Value.Username);
        Assert.Equal(RatingPools.Visible.Length, up.Value.Ratings.Length);   // Ranked, Daily, Arena Ranked Ladder
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("a_very_long_name_x", false)]
    [InlineData("admin", false)]
    [InlineData("bad-char", false)]
    [InlineData("xx_fuck_1", false)]
    [InlineData("Good_Name_1", true)]
    public void UsernameRulesValidate(string raw, bool ok)
        => Assert.Equal(ok, new UsernameRules(new TicTacToeUltimate.Server.Social.ProfanityFilter()).IsValid(UsernameRules.Normalize(raw)));

    [Fact]
    public async Task OneFreeRenameThenNoRenamesLeft()
    {
        using var p = new TestPlatform();
        var accounts = p.Get<AccountService>();
        var me = (await accounts.DevTokenAsync(new DevTokenRequestDto(null, N("rn_")), default)).Value!.Me;
        Assert.Equal(1, me.RenamesLeft);
        var r1 = await accounts.SetUsernameAsync(me.UserId, new SetUsernameDto(N("rn_"), false), default);
        Assert.True(r1.Ok);
        Assert.Equal(0, r1.Value!.RenamesLeft);
        Assert.Equal(ErrorCodes.NoRenamesLeft, (await accounts.SetUsernameAsync(me.UserId, new SetUsernameDto(N("rn_"), false), default)).Error);
        Assert.Equal(ErrorCodes.UsernameInvalid, (await accounts.SetUsernameAsync(me.UserId, new SetUsernameDto("root", false), default)).Error);
    }

    [Fact]
    public async Task ClaimsTransformationCreatesSupabaseGuestAndRefreshesKind()
    {
        using var p = new TestPlatform();
        var sub = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim("sub", sub.ToString()), new Claim("is_anonymous", "true")], "Supabase");
        var tx = p.Services.CreateScope().ServiceProvider.GetRequiredService<IClaimsTransformation>();
        var principal = await tx.TransformAsync(new ClaimsPrincipal(identity));
        var user = principal.ToTttUser();
        Assert.Equal((sub, AccountKind.Guest), (user.UserId, user.Kind));
        Assert.NotNull(await p.Get<IUserRepository>().GetAsync(sub, default));

        var email = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("email", "jane.doe@example.com")], "Supabase");
        var acct = (await tx.TransformAsync(new ClaimsPrincipal(email))).ToTttUser();
        Assert.Equal((AccountKind.Account, "jane.doe"), (acct.Kind, acct.DisplayName));

        // Stale guest claim is refreshed from the store after an upgrade.
        await p.Get<IUserRepository>().SetUsernameAsync(sub, N("sb_"), TestPlatform.Start, default);
        var stale = new ClaimsIdentity([new Claim("sub", sub.ToString()), new Claim(TttClaims.Kind, "guest")], "Dev");
        Assert.Equal(AccountKind.Account, (await tx.TransformAsync(new ClaimsPrincipal(stale))).ToTttUser().Kind);
    }
}
