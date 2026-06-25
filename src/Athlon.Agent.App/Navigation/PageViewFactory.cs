using System.Windows.Controls;
using Athlon.Agent.App.Views;

namespace Athlon.Agent.App.Navigation;

public sealed class PageViewFactory
{
    private readonly Dictionary<AppPage, Lazy<UserControl>> _pages = new();

    public UserControl GetOrCreate(AppPage page)
    {
        if (!_pages.TryGetValue(page, out var lazy))
        {
            lazy = new Lazy<UserControl>(() => CreateView(page));
            _pages[page] = lazy;
        }

        return lazy.Value;
    }

    public void Preload(AppPage page) => _ = GetOrCreate(page);

    private static UserControl CreateView(AppPage page) =>
        page switch
        {
            AppPage.Chat => new ChatPageView(),
            AppPage.Settings => new SettingsPageView(),
            AppPage.Knowledge => new KnowledgePageView(),
            AppPage.Schedule => new SchedulePageView(),
            _ => new ChatPageView()
        };
}
