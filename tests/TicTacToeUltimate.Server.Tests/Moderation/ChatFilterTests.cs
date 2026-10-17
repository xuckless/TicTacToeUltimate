using TicTacToeUltimate.Server.Moderation;

namespace TicTacToeUltimate.Server.Tests.Moderation;

public sealed class ChatFilterTests
{
    private static readonly ChatFilter Filter = new();

    [Theory]
    [InlineData("you f u c k i n g idiot")]
    [InlineData("F.U.C.K off")]
    [InlineData("fuuuuuck")]
    [InlineData("sh1t player")]
    [InlineData("ты сука")]
    [InlineData("puta madre")]
    [InlineData("vai tomar no cu, caralho")]
    [InlineData("死ね")]
    [InlineData("씨발")]
    public void Blocks_abuse_including_tricks(string text) => Assert.Equal(FilterVerdict.Block, Filter.Check(text).Verdict);

    [Theory]
    [InlineData("gg wp, nice game")]
    [InlineData("I grew up in Scunthorpe")]
    [InlineData("các bạn chơi hay quá")]
    [InlineData("grapes and spices")]
    [InlineData("明日やくそくね")]
    [InlineData("coffee cup כוס קפה")]
    public void Lets_normal_text_through(string text) => Assert.NotEqual(FilterVerdict.Block, Filter.Check(text).Verdict);

    [Fact]
    public void Mild_words_and_hidden_severe_words_go_to_the_ai()
    {
        Assert.Equal(FilterVerdict.Uncertain, Filter.Check("you are such an idiot").Verdict);
        Assert.Equal(FilterVerdict.Uncertain, Filter.Check("I grew up in Scunthorpe").Verdict);
    }

    [Theory]
    [InlineData("add me on www.example.com")]
    [InlineData("https://bit.ly/x")]
    [InlineData("mail me at a.b@example.org")]
    [InlineData("call +1 555 123 4567")]
    public void Links_emails_and_phones_are_rejected_without_a_strike(string text) => Assert.Equal(FilterVerdict.Reject, Filter.Check(text).Verdict);

    [Fact]
    public void Own_links_are_fine() => Assert.Equal(FilterVerdict.Allow, Filter.Check("join me: tictactoeultimate.alidirect.dev/c/ABCD").Verdict);

    [Theory]
    [InlineData("xx_f4gg0t_xx", true)]
    [InlineData("shit_lord", true)]
    [InlineData("motherfucker99", true)]
    [InlineData("scunthorpe_fan", false)]
    [InlineData("grape_master", false)]
    [InlineData("ali_xo", false)]
    public void Usernames(string name, bool blocked) => Assert.Equal(blocked, Filter.UsernameBlocked(name));
}
