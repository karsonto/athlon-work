using System.Text.Json;

namespace Athlon.Agent.Core.TrainingData;

/// <summary>
/// 检测对话历史中的"失败→修正→成功"模式。
///
/// 检测逻辑：
/// 1. 找到所有失败的 tool call（从 tool log 中识别）
/// 2. 对每个失败，向后查找最近的 user 消息（修正指令）
/// 3. 在修正指令之后，查找同名的工具调用成功
/// 4. 如果找到，记录为一条 CorrectionTrajectory
/// </summary>
public static class CorrectionDetector
{
    /// <summary>
    /// 一条"失败→修正→成功"轨迹。
    /// </summary>
    public sealed record CorrectionTrajectory(
        /// <summary>失败的工具调用</summary>
        AgentToolCall FailedToolCall,
        /// <summary>失败消息在 Messages 中的索引</summary>
        int FailureMessageIndex,
        /// <summary>发出失败 tool call 的 assistant 消息</summary>
        ChatMessage FailureAssistantMessage,
        /// <summary>修正指令（用户的下一轮输入）</summary>
        ChatMessage CorrectionMessage,
        /// <summary>修正后成功的工具调用</summary>
        AgentToolCall SuccessfulToolCall);

    /// <summary>
    /// 一条"超时/溢出→用户继续→成功完成"轨迹。
    /// </summary>
    public sealed record OverflowRecoveryTrajectory(
        /// <summary>超时/终止通知消息（System 角色）</summary>
        ChatMessage OverflowNotice,
        /// <summary>通知消息在 Messages 中的索引</summary>
        int NoticeIndex,
        /// <summary>用户的继续指令</summary>
        ChatMessage ContinuationMessage,
        /// <summary>继续后成功完成的最后一条 assistant 回复</summary>
        ChatMessage RecoveryAssistantMessage);

    /// <summary>
    /// 从对话消息中检测修正轨迹。
    /// 通过工具 result 的内容判断成功/失败（result 以 "Error:" 开头视为失败）。
    /// </summary>
    public static IReadOnlyList<CorrectionTrajectory> Detect(
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return Array.Empty<CorrectionTrajectory>();

        // 找出所有失败的工具调用: assistant 发出了 tool call,
        // 后续对应的 tool result 包含错误信息
        var failures = FindFailedToolCalls(messages);

        if (failures.Count == 0)
            return Array.Empty<CorrectionTrajectory>();

        var trajectories = new List<CorrectionTrajectory>();

        foreach (var (failureMsgIndex, failedCall) in failures)
        {
            // 向后查找下一个 user 消息（修正指令）
            var correctionMsgIndex = FindNextUserMessage(messages, failureMsgIndex + 1);
            if (correctionMsgIndex < 0)
                continue;

            var correctionMessage = messages[correctionMsgIndex];

            // 在修正指令之后，查找同名的工具调用成功
            var successCall = FindSuccessfulRetry(messages, correctionMsgIndex + 1, failedCall.Name);
            if (successCall is null)
                continue;

            trajectories.Add(new CorrectionTrajectory(
                failedCall,
                failureMsgIndex,
                messages[failureMsgIndex],
                correctionMessage,
                successCall));
        }

        return trajectories;
    }

    /// <summary>
    /// 找出所有失败的工具调用。
    /// 判断逻辑：assistant 发出 tool call，后续 tool result 以 Error: 开头。
    /// </summary>
    private static List<(int MessageIndex, AgentToolCall FailedCall)> FindFailedToolCalls(
        IReadOnlyList<ChatMessage> messages)
    {
        var results = new List<(int, AgentToolCall)>();

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != MessageRole.Assistant)
                continue;

            var toolCalls = AssistantToolCallsCodec.Deserialize(messages[i].ToolCallsJson);
            if (toolCalls is null || toolCalls.Count == 0)
                continue;

            foreach (var tc in toolCalls)
            {
                // 找到这个 tool call 对应的 result（后续的 tool 消息）
                for (var j = i + 1; j < messages.Count; j++)
                {
                    if (messages[j].Role != MessageRole.Tool)
                        continue;

                    var tId = ModelMessageBuilder.ExtractToolCallId(messages[j].Content);
                    if (tId != tc.Id)
                        continue;

                    // 判断是否失败（以 Error: 开头或包含错误标记）
                    if (IsFailedToolResult(messages[j].Content))
                    {
                        results.Add((i, tc));
                    }
                    break; // 找到对应的 tool result，无论成功失败都停止
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 判断工具结果是否表示失败。
    /// 实际格式（ModelMessageBuilder.FormatToolResult）：
    ///   ToolCallId: call_abc
    ///   Tool `file_read` failed.
    ///   ...
    /// </summary>
    private static bool IsFailedToolResult(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return true; // 空结果视为失败

        // 实际 tool result 格式中包含 "Tool `xxx` failed."
        if (content.Contains("failed.", StringComparison.OrdinalIgnoreCase)
            && content.Contains("Tool `", StringComparison.Ordinal))
            return true;

        // 备选：常见的失败标记（兼容其他格式）
        if (content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (content.StartsWith("失败:", StringComparison.Ordinal))
            return true;

        if (content.Contains("\"isError\": true", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 从 startIndex 开始查找下一个 user 消息。
    /// </summary>
    private static int FindNextUserMessage(IReadOnlyList<ChatMessage> messages, int startIndex)
    {
        for (var i = startIndex; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.User)
                return i;
        }

        return -1;
    }

    // =================================================================
    // 超时/溢出恢复检测
    // =================================================================

    /// <summary>
    /// 检测"超时/溢出→用户继续→恢复成功"模式。
    ///
    /// 匹配条件：
    /// 1. System 消息包含超时/停止/模型调用失败标记
    /// 2. 下一条 User 消息是继续指令（"继续"、"接着"、"continue" 等）
    /// 3. 继续后有 assistant 完整回复
    /// </summary>
    public static IReadOnlyList<OverflowRecoveryTrajectory> DetectOverflowRecoveries(
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return Array.Empty<OverflowRecoveryTrajectory>();

        var results = new List<OverflowRecoveryTrajectory>();

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != MessageRole.System)
                continue;

            if (!IsOverflowNotice(messages[i].Content))
                continue;

            // 找到这之后的第一个 user 消息（继续指令）
            var userIndex = FindNextUserMessage(messages, i + 1);
            if (userIndex < 0)
                continue;

            var userMsg = messages[userIndex];
            if (!IsContinuationRequest(userMsg.Content))
                continue;

            // 找到继续后最后的 assistant 回复
            var recoveryAssistant = FindLastAssistantAfter(messages, userIndex + 1);
            if (recoveryAssistant is null)
                continue;

            results.Add(new OverflowRecoveryTrajectory(
                messages[i],
                i,
                userMsg,
                recoveryAssistant));
        }

        return results;
    }

    /// <summary>
    /// 判断消息内容是否表示超时/溢出/被终止。
    /// </summary>
    private static bool IsOverflowNotice(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // SessionTurnReconciler 写入的超时标记
        if (content.Contains("超过配置的超时时间", StringComparison.Ordinal))
            return true;

        if (content.Contains("已自动停止", StringComparison.Ordinal))
            return true;

        if (content.Contains("生成已停止", StringComparison.Ordinal))
            return true;

        // TurnFailureMessages 写入的模型调用失败
        if (content.StartsWith("模型调用失败：", StringComparison.Ordinal))
            return true;

        // 通用的 overflow / timeout 标记
        if (content.Contains("context_length", StringComparison.OrdinalIgnoreCase))
            return true;

        if (content.Contains("超时", StringComparison.Ordinal))
            return true;

        if (content.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 判断用户消息是否是一个"继续"请求。
    /// </summary>
    private static bool IsContinuationRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // 中文继续标记
        if (content == "继续" || content.StartsWith("继续", StringComparison.Ordinal))
            return true;

        if (content == "接着" || content.StartsWith("接着", StringComparison.Ordinal))
            return true;

        if (content.StartsWith("继续分析", StringComparison.Ordinal))
            return true;

        if (content.StartsWith("接着分析", StringComparison.Ordinal))
            return true;

        if (content.StartsWith("接着做", StringComparison.Ordinal))
            return true;

        if (content.StartsWith("继续说", StringComparison.Ordinal))
            return true;

        if (content.StartsWith("继续做", StringComparison.Ordinal))
            return true;

        // 英文继续标记
        var trimmed = content.Trim().ToLowerInvariant();
        if (trimmed is "continue" or "go on" or "keep going")
            return true;

        if (trimmed.StartsWith("continue ", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// 在 startIndex 之后查找最后一条 assistant 消息（有内容或有 tool calls）。
    /// </summary>
    private static ChatMessage? FindLastAssistantAfter(IReadOnlyList<ChatMessage> messages, int startIndex)
    {
        ChatMessage? last = null;
        for (var i = startIndex; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.Assistant)
            {
                var hasContent = !string.IsNullOrWhiteSpace(messages[i].Content);
                var hasTools = !string.IsNullOrWhiteSpace(messages[i].ToolCallsJson)
                    && messages[i].ToolCallsJson != "[]";
                if (hasContent || hasTools)
                {
                    last = messages[i];
                }
            }
        }
        return last;
    }

    /// <summary>
    /// 在修正指令后查找同名工具的成功调用。
    /// 成功判断逻辑：对应的 tool result 不含错误标记。
    /// </summary>
    private static AgentToolCall? FindSuccessfulRetry(
        IReadOnlyList<ChatMessage> messages,
        int startIndex,
        string toolName)
    {
        for (var i = startIndex; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == MessageRole.User)
            {
                // 下一个用户消息 = 新的一轮，停止搜索
                break;
            }

            if (msg.Role != MessageRole.Assistant)
                continue;

            var toolCalls = AssistantToolCallsCodec.Deserialize(msg.ToolCallsJson);
            if (toolCalls is null || toolCalls.Count == 0)
                continue;

            foreach (var tc in toolCalls)
            {
                if (!string.Equals(tc.Name, toolName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 找到这个 tool call 对应的 result
                for (var j = i + 1; j < messages.Count; j++)
                {
                    if (messages[j].Role != MessageRole.Tool)
                        continue;

                    var tId = ModelMessageBuilder.ExtractToolCallId(messages[j].Content);
                    if (tId != tc.Id)
                        continue;

                    // 如果没有错误标记，视为成功
                    if (!IsFailedToolResult(messages[j].Content))
                    {
                        return tc;
                    }
                    break;
                }
            }
        }

        return null;
    }
}
