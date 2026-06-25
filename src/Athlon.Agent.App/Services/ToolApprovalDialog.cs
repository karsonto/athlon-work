using System.Text;
using System.Windows;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

internal static class ToolApprovalDialog
{
    public static ToolApprovalDecision Show(PendingToolApproval pending)
    {
        var body = new StringBuilder();
        body.AppendLine($"工具：{pending.ToolName}");
        body.AppendLine();
        foreach (var pair in pending.Arguments.OrderBy(argument => argument.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            if (value.Length > 500)
            {
                value = value[..500] + "…";
            }

            body.AppendLine($"{pair.Key}: {value}");
        }

        var result = MessageBox.Show(
            body.ToString(),
            "确认工具调用",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => ToolApprovalDecision.Approved,
            MessageBoxResult.No => ToolApprovalDecision.Rejected,
            _ => ToolApprovalDecision.Cancelled
        };
    }
}
