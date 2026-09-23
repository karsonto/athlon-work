using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.Resources;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.Windows;
using Athlon.Agent.Infrastructure.Ssh;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.ViewModels;

/// <summary>
/// Computer-use overlay status: refreshing the overlay status text and tracking the chat
/// messages that carry it. Split out of the shell orchestration file.
/// </summary>
public partial class MainShellViewModel
{
    private const int ComputerUseActionLogLimit = 6;

    private void RefreshComputerUseStatus()
    {
        OnPropertyChanged(nameof(ComputerUseStatusVisible));
        OnPropertyChanged(nameof(HasComputerUseTranscript));
        RefreshComputerUseActionLog();
        if (!ComputerUseStatusVisible)
        {
            ComputerUseActiveToolText = string.Empty;
            ComputerUseAssistantSummary = string.Empty;
            ComputerUseActionLog = [];
            return;
        }

        var tool = ComputerUseStatusFormatter.FindLatestComputerUseTool(Messages);
        ComputerUseActiveToolText = ComputerUseStatusFormatter.FormatToolLine(
            tool?.ToolName,
            tool?.ToolStatusLabel,
            Strings.Get("ComputerUse_StatusThinking"),
            Strings.Get("ComputerUse_StatusToolFormat"));

        var assistant = ComputerUseStatusFormatter.FindLatestAssistantWithContent(Messages);
        ComputerUseAssistantSummary = ComputerUseStatusFormatter.FormatAssistantSummary(assistant?.Content);
    }

    /// <summary>
    /// Compact action log for the overlay. The overlay transcript deliberately hides tool cards,
    /// which left users with no signal about what the agent was doing; this surfaces the most
    /// recent actions as one line each.
    /// </summary>
    private void RefreshComputerUseActionLog()
    {
        var lines = new List<string>(ComputerUseActionLogLimit);
        for (var index = Messages.Count - 1; index >= 0 && lines.Count < ComputerUseActionLogLimit; index--)
        {
            var line = Messages[index].ComputerUseActionLine;
            if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }
        }

        if (lines.Count == 0)
        {
            ComputerUseActionLog = [];
            return;
        }

        // Collected newest-first; the overlay reads top-down like a log.
        lines.Reverse();
        ComputerUseActionLog = lines;
    }

    private void AttachComputerUseStatusMessageListeners()
    {
        DetachComputerUseStatusMessageListeners();
        foreach (var message in Messages)
        {
            SubscribeComputerUseStatusMessage(message);
        }
    }

    private void DetachComputerUseStatusMessageListeners()
    {
        foreach (var message in _computerUseStatusMessageSubscriptions.ToArray())
        {
            UnsubscribeComputerUseStatusMessage(message);
        }
    }

    private void SyncComputerUseStatusMessageSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            AttachComputerUseStatusMessageListeners();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (ChatMessageViewModel message in e.OldItems)
            {
                UnsubscribeComputerUseStatusMessage(message);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessageViewModel message in e.NewItems)
            {
                SubscribeComputerUseStatusMessage(message);
            }
        }
    }

    private void SubscribeComputerUseStatusMessage(ChatMessageViewModel message)
    {
        if (!_computerUseStatusMessageSubscriptions.Add(message))
        {
            return;
        }

        message.PropertyChanged += OnComputerUseStatusMessagePropertyChanged;
    }

    private void UnsubscribeComputerUseStatusMessage(ChatMessageViewModel message)
    {
        if (!_computerUseStatusMessageSubscriptions.Remove(message))
        {
            return;
        }

        message.PropertyChanged -= OnComputerUseStatusMessagePropertyChanged;
    }

    private void OnComputerUseStatusMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Content)
            or nameof(ChatMessageViewModel.ToolName)
            or nameof(ChatMessageViewModel.ToolArgumentsText)
            or nameof(ChatMessageViewModel.ToolCallStatus)
            or nameof(ChatMessageViewModel.ToolApprovalState)
            or nameof(ChatMessageViewModel.IsStreaming)
            or nameof(ChatMessageViewModel.IsToolRunning)
            or nameof(ChatMessageViewModel.ToolStatusLabel))
        {
            RefreshComputerUseStatus();
        }
    }

    partial void OnIsCompactingChanged(bool value) => NotifyComposerCompactionStateChanged();

    partial void OnComputerUseActionLogChanged(IReadOnlyList<string> value) =>
        OnPropertyChanged(nameof(HasComputerUseActionLog));
}
