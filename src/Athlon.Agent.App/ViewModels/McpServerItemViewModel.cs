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

public sealed partial class McpServerItemViewModel : ObservableObject
{
    private readonly IMcpRegistry _mcpRegistry;
    private readonly Action? _onEnabledChanged;

    public McpServerItemViewModel(McpServerSettings settings, IMcpRegistry mcpRegistry, Action? onEnabledChanged = null)
    {
        Settings = settings;
        _mcpRegistry = mcpRegistry;
        _onEnabledChanged = onEnabledChanged;
    }

    public McpServerSettings Settings { get; }

    public void RefreshRuntimeState()
    {
        OnPropertyChanged(nameof(RuntimeState));
        OnPropertyChanged(nameof(ToolSummary));
        OnPropertyChanged(nameof(ShowStatusDot));
        OnPropertyChanged(nameof(IsStatusHealthy));
        OnPropertyChanged(nameof(IsStatusError));
    }

    public string DisplayInitial => string.IsNullOrWhiteSpace(Name) ? "M" : Name.Trim()[0].ToString().ToUpperInvariant();

    public string Name
    {
        get => Settings.Name;
        set
        {
            if (Settings.Name == value)
            {
                return;
            }

            Settings.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayInitial));
        }
    }

    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value)
            {
                return;
            }

            Settings.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ToolSummary));
            OnPropertyChanged(nameof(ShowStatusDot));
            OnPropertyChanged(nameof(IsStatusHealthy));
            OnPropertyChanged(nameof(IsStatusError));
            _onEnabledChanged?.Invoke();
        }
    }

    public Athlon.Agent.Mcp.McpConnectionState RuntimeState
    {
        get
        {
            if (!Enabled)
            {
                return Athlon.Agent.Mcp.McpConnectionState.Disabled;
            }

            return _mcpRegistry.GetStatuses()
                .FirstOrDefault(item => string.Equals(item.Name, Name, StringComparison.OrdinalIgnoreCase))
                ?.State ?? Athlon.Agent.Mcp.McpConnectionState.Connecting;
        }
    }

    public bool ShowStatusDot => Enabled;

    public bool IsStatusHealthy =>
        RuntimeState == Athlon.Agent.Mcp.McpConnectionState.Connected;

    public bool IsStatusError =>
        RuntimeState == Athlon.Agent.Mcp.McpConnectionState.Error;

    public string Command
    {
        get => Settings.Command;
        set
        {
            if (Settings.Command == value)
            {
                return;
            }

            Settings.Command = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string ArgsText
    {
        get => string.Join(" ", Settings.Args);
        set
        {
            Settings.Args.Clear();
            if (!string.IsNullOrWhiteSpace(value))
            {
                Settings.Args.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CommandSummary));
        }
    }

    public string StatusText => Enabled ? "Configured and enabled" : "Configured but disabled";

    public string ToolSummary
    {
        get
        {
            if (!Enabled)
            {
                return "Disabled";
            }

            var status = _mcpRegistry.GetStatuses().FirstOrDefault(item => string.Equals(item.Name, Name, StringComparison.OrdinalIgnoreCase));
            return McpRuntimeStatusText.ToolSummary(status);
        }
    }

    public string CommandSummary
    {
        get
        {
            var args = ArgsText;
            return string.IsNullOrWhiteSpace(args) ? $"command: {Command}" : $"command: {Command} {args}";
        }
    }
}
