using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Athlon.Agent.App.ViewModels;

public sealed class SessionHistoryItemViewModel
{
    public SessionHistoryItemViewModel(SessionIndexEntry entry, bool isActive)
    {
        Id = entry.Id;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "未命名对话" : entry.Title;
        UpdatedAtText = entry.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
        IsActive = isActive;
    }

    public string Id { get; }
    public string Title { get; }
    public string UpdatedAtText { get; }
    public bool IsActive { get; }
}
