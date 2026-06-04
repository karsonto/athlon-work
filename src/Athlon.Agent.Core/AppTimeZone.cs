namespace Athlon.Agent.Core;

/// <summary>Application-wide China Standard Time (UTC+8) for prompts and UI timestamps.</summary>
public static class AppTimeZone
{
    public const string PromptLabel = "UTC+8";

    private static readonly TimeZoneInfo ChinaStandard = ResolveChinaStandardTimeZone();

    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ChinaStandard);

    public static DateTime Today => Now.Date;

    public static DateTime ToChinaDate(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, ChinaStandard).Date;

    public static DateTimeOffset ToChina(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, ChinaStandard);

    private static TimeZoneInfo ResolveChinaStandardTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }
}
