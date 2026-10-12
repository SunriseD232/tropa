using Tropa.Core.Diagnostics;

namespace Tropa.Core.Tests.Diagnostics;

public sealed class ExitRegionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_country_from_trace()
    {
        var r = ExitRegion.Parse("fl=1f1\nh=www.cloudflare.com\nip=203.0.113.5\nts=1\nvisit_scheme=https\ncolo=AMS\nloc=NL\ntls=TLSv1.3\n", Now);
        Assert.NotNull(r);
        Assert.Equal("NL", r.CountryCode);
        Assert.Equal("Нидерланды", r.Name);
        Assert.Equal(Now, r.CheckedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ip=1.2.3.4\n")]
    [InlineData("loc=XX\n")]
    [InlineData("loc=T1\n")]
    [InlineData("loc=<script>\n")]
    public void Unknown_or_garbage_is_null(string? body) => Assert.Null(ExitRegion.Parse(body, Now));

    [Fact]
    public void Unknown_code_falls_back_to_code() => Assert.Equal("ZA", ExitRegion.Parse("loc=za", Now)!.Name);
}
