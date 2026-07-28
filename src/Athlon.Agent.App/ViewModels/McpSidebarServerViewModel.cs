using System.Collections.ObjectModel;
using Athlon.Agent.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athlon.Agent.App.ViewModels;

public sealed partial class McpSidebarServerViewModel : ObservableObject
{
    private readonly Func<string, Task>? _onActivate;

    public McpSidebarServerViewModel(
        string name,
        bool enabled,
        McpServerStatus? status,
        bool isExpanded = false,
        Func<string, Task>? onActivate = null)
    {
        Name = name;
        Enabled = enabled;
        RuntimeState = enabled ? status?.State ?? McpConnectionState.Connecting : McpConnectionState.Disabled;
        Summary = BuildSummary(enabled, status);
        ToolNames = new ObservableCollection<string>(
            enabled && status?.State == McpConnectionState.Connected
                ? status.Tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                : Array.Empty<string>());
        IsExpanded = isExpanded && ToolNames.Count > 0;
        _onActivate = onActivate;
    }

    public string Name { get; }
    public bool Enabled { get; }
    public McpConnectionState RuntimeState { get; }
    public string Summary { get; }
    public ObservableCollection<string> ToolNames { get; }
    public bool HasTools => ToolNames.Count > 0;
    public bool IsDisabled => !Enabled;
    public bool IsHealthy => Enabled && RuntimeState == McpConnectionState.Connected;
    public bool IsWarning => Enabled && RuntimeState != McpConnectionState.Connected;
    public string Chevron => IsExpanded ? "▾" : "▸";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool isExpanded;

    [RelayCommand]
    private void ToggleExpanded()
    {
        if (HasTools)
        {
            IsExpanded = !IsExpanded;
        }
    }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (_onActivate is null || string.IsNullOrWhiteSpace(Name))
        {
            return;
        }

        await _onActivate(Name).ConfigureAwait(true);
    }

    private static string BuildSummary(bool enabled, McpServerStatus? status)
    {
        if (!enabled)
        {
            return "Disabled";
        }

        return status?.State switch
        {
            McpConnectionState.Connected => $"{status.Tools.Count} tools",
            McpConnectionState.Error => $"Error · {status.LastError}",
            McpConnectionState.Connecting => "Connecting...",
            _ => "Not connected"
        };
    }
}
