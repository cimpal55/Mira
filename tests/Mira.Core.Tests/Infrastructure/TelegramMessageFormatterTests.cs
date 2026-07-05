namespace Mira.Core.Tests.Infrastructure;

using Mira.Infrastructure.Telegram;
using Xunit;

public sealed class TelegramMessageFormatterTests
{
    [Fact]
    public void ToTelegramHtml_converts_commonmark_bold_heading_to_telegram_bold()
    {
        var html = TelegramMessageFormatter.ToTelegramHtml("**Daily Brief**");

        Assert.Equal("<b>Daily Brief</b>", html);
    }

    [Fact]
    public void ToTelegramHtml_converts_bold_label_inside_telegram_bullet_like_line()
    {
        var html = TelegramMessageFormatter.ToTelegramHtml("*   **Reminders:** None.");

        Assert.Equal("*   <b>Reminders:</b> None.", html);
    }

    [Fact]
    public void ToTelegramHtml_escapes_html_outside_and_inside_bold_text()
    {
        var html = TelegramMessageFormatter.ToTelegramHtml("Use <tag> & **5 > 3 & <safe>**");

        Assert.Equal("Use &lt;tag&gt; &amp; <b>5 &gt; 3 &amp; &lt;safe&gt;</b>", html);
    }

    [Theory]
    [InlineData("This **bold never closes <ok>", "This **bold never closes &lt;ok&gt;")]
    [InlineData("This bold never opens** <ok>", "This bold never opens** &lt;ok&gt;")]
    public void ToTelegramHtml_keeps_unmatched_bold_delimiters_literal_and_escapes_text(string input, string expected)
    {
        var html = TelegramMessageFormatter.ToTelegramHtml(input);

        Assert.Equal(expected, html);
    }
}