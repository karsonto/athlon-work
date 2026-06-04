using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class AppTimeZoneTests
{
    [Fact]
    public void ToChina_AppliesUtcPlusEightOffset()
    {
        var utc = new DateTimeOffset(2026, 6, 4, 10, 30, 0, TimeSpan.Zero);
        var china = AppTimeZone.ToChina(utc);

        Assert.Equal(TimeSpan.FromHours(8), china.Offset);
        Assert.Equal(18, china.Hour);
        Assert.Equal(30, china.Minute);
    }

    [Fact]
    public void ToChinaDate_UsesChinaCalendarDate()
    {
        var utc = new DateTimeOffset(2026, 6, 4, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 6, 5), AppTimeZone.ToChinaDate(utc));
    }
}
