namespace Athlon.Agent.App.Navigation;

public enum AppPage
{
    Chat,
    Settings,
    Knowledge,
    Schedule
}

public static class AppPageExtensions
{
    public static AppPage Parse(string? page) =>
        page?.Trim() switch
        {
            nameof(AppPage.Settings) => AppPage.Settings,
            nameof(AppPage.Knowledge) => AppPage.Knowledge,
            nameof(AppPage.Schedule) => AppPage.Schedule,
            _ => AppPage.Chat
        };

    public static string ToPageKey(this AppPage page) => page.ToString();
}
