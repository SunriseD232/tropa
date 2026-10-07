using Tropa.Core.Routing;

namespace Tropa.Core.Tests.Routing;

public sealed class RuleInputTests
{
    [Theory]
    [InlineData("Discord.exe", "Discord.exe")]
    [InlineData("discord", "discord.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\app-1.0\Discord.exe", "Discord.exe")]
    [InlineData("\"Telegram.exe\"", "Telegram.exe")]
    [InlineData("VALORANT-Win64-Shipping.exe", "VALORANT-Win64-Shipping.exe")]
    [InlineData("Яндекс Браузер.exe", "Яндекс Браузер.exe")]
    public void Process_names(string input, string expected) => Assert.Equal(expected, RuleInput.NormalizeProcess(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil\".exe")]
    [InlineData("a*b.exe")]
    [InlineData("a|b")]
    public void Bad_process_names(string input) => Assert.Null(RuleInput.NormalizeProcess(input));

    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData("https://www.YouTube.com/watch?v=1", "youtube.com")]
    [InlineData("*.example.org", "example.org")]
    [InlineData("discord.gg:443", "discord.gg")]
    [InlineData("госуслуги.рф", "xn--c1aapkosapc.xn--p1ai")]
    public void Domains(string input, string expected) => Assert.Equal(expected, RuleInput.NormalizeDomain(input));

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("-bad-.com")]
    [InlineData("a b.com")]
    [InlineData("example..com")]
    public void Bad_domains(string input) => Assert.Null(RuleInput.NormalizeDomain(input));
}
