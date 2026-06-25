import subprocess
import pathlib

root = pathlib.Path(__file__).resolve().parents[1]
text = subprocess.check_output(
    ["git", "-C", str(root), "show", "HEAD:src/Athlon.Agent.App/MainWindow.xaml"]
)
content = text.decode("utf-8-sig")
lines = content.splitlines()
views = root / "src/Athlon.Agent.App/Views"

headers = {
    "ChatPageView": """<UserControl x:Class="Athlon.Agent.App.Views.ChatPageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:app="clr-namespace:Athlon.Agent.App"
             xmlns:behaviors="clr-namespace:Athlon.Agent.App.Behaviors"
             xmlns:controls="clr-namespace:Athlon.Agent.App.Controls"
             xmlns:i="http://schemas.microsoft.com/xaml/behaviors"
             xmlns:vm="clr-namespace:Athlon.Agent.App.ViewModels">
    <Grid Background="{DynamicResource Brush.ChatBackground}" ClipToBounds="True">""",
    "SettingsPageView": """<UserControl x:Class="Athlon.Agent.App.Views.SettingsPageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:behaviors="clr-namespace:Athlon.Agent.App.Behaviors"
             xmlns:vm="clr-namespace:Athlon.Agent.App.ViewModels">
    <ScrollViewer Padding="40,32" VerticalScrollBarVisibility="Auto" Background="{DynamicResource Brush.ChatBackground}">""",
    "KnowledgePageView": """<UserControl x:Class="Athlon.Agent.App.Views.KnowledgePageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:app="clr-namespace:Athlon.Agent.App"
             xmlns:controls="clr-namespace:Athlon.Agent.App.Controls">
    <Grid Background="{DynamicResource Brush.ChatBackground}">""",
    "SchedulePageView": """<UserControl x:Class="Athlon.Agent.App.Views.SchedulePageView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Athlon.Agent.App.ViewModels">
    <ScrollViewer Padding="40,32" VerticalScrollBarVisibility="Auto" Background="{DynamicResource Brush.ChatBackground}">""",
    "ContextSidebarView": """<UserControl x:Class="Athlon.Agent.App.Views.ContextSidebarView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:behaviors="clr-namespace:Athlon.Agent.App.Behaviors"
             xmlns:controls="clr-namespace:Athlon.Agent.App.Controls"
             xmlns:i="http://schemas.microsoft.com/xaml/behaviors">""",
}

footers = {
    "ChatPageView": "    </Grid>\n</UserControl>",
    "SettingsPageView": "    </ScrollViewer>\n</UserControl>",
    "KnowledgePageView": "    </Grid>\n</UserControl>",
    "SchedulePageView": "    </ScrollViewer>\n</UserControl>",
    "ContextSidebarView": "</UserControl>",
}

ranges = {
    "ChatPageView": (168, 726),
    "SettingsPageView": (744, 1203),
    "KnowledgePageView": (1219, 1503),
    "SchedulePageView": (1521, 1931),
    "ContextSidebarView": (1998, 2236),
}

for name, (start, end) in ranges.items():
    inner = "\n".join(lines[start - 1 : end])
    out = headers[name] + "\n" + inner + "\n" + footers[name] + "\n"
    (views / f"{name}.xaml").write_text(out, encoding="utf-8")

print(f"Extracted {len(ranges)} views from {len(lines)} lines")
