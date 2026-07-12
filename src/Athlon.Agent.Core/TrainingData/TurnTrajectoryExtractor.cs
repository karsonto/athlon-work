using System.Text.Json;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core.TrainingData;

/// <summary>
/// 将 AgentSession 的对话历史提取为 TrainingSample 列表。
/// 支持提取完整会话、单轮、或仅含修正的轨迹。
/// </summary>
public static class TurnTrajectoryExtractor
{
    /// <summary>
    /// 从 session 中提取所有修正轨迹作为训练样本。
    /// 每条修正轨迹 = 从失败前最近的 user 到最终成功的完整对话切片。
    /// </summary>
    public static IReadOnlyList<TrainingSample> ExtractCorrectionSamples(
        AgentSession session)
    {
        var trajectories = CorrectionDetector.Detect(session.Messages);
        var samples = new List<TrainingSample>();

        foreach (var traj in trajectories)
        {
            // 从失败所在的 user 消息开始，到成功后的 assistant 回复结束
            var startIndex = FindTurnStartIndex(session.Messages, traj.FailureMessageIndex);
            var endIndex = FindTrajectoryEndIndex(session.Messages, traj.SuccessfulToolCall);

            var messages = session.Messages
                .Skip(startIndex)
                .Take(endIndex - startIndex + 1)
                .ToList();

            // 统计 token（使用估算值）
            var totalTokens = EstimateMessagesTokens(messages);

            var sample = new TrainingSample
            {
                Messages = ConvertMessages(messages),
                Metadata = new TrainingMetadata
                {
                    Source = "agent-correction",
                    SessionId = session.Id,
                    ExtractedAt = DateTime.UtcNow,
                    HasCorrection = true,
                    Model = session.ModelName,
                    ToolCallCount = messages.Count(m => m.Role == MessageRole.Assistant),
                    TotalTokens = totalTokens,
                    Score = ComputeQualityScore(traj),
                    FailedToolCallIds = [traj.FailedToolCall.Id],
                    CorrectionSummary = Truncate(traj.CorrectionMessage.Content, 200),
                    HasReasoning = messages.Any(m => !string.IsNullOrWhiteSpace(m.ReasoningContent)),
                    ReasoningChainLength = CountReasoningChain(messages)
                }
            };

            samples.Add(sample);
        }

        return samples;
    }

    /// <summary>
    /// 从 session 中提取所有超时/溢出恢复轨迹作为训练样本。
    /// 每条恢复轨迹 = 从溢出通知到用户继续再到最终助理回复的完整切片。
    /// </summary>
    public static IReadOnlyList<TrainingSample> ExtractOverflowRecoverySamples(
        AgentSession session)
    {
        var recoveries = CorrectionDetector.DetectOverflowRecoveries(session.Messages);
        var samples = new List<TrainingSample>();

        foreach (var rec in recoveries)
        {
            // 从溢出通知的 System 消息开始，到恢复后的 assistant 回复结束
            var startIndex = rec.NoticeIndex;
            var endIndex = FindMessageIndex(session.Messages, rec.RecoveryAssistantMessage.Id);

            if (endIndex < startIndex)
                continue;

            var messages = session.Messages
                .Skip(startIndex)
                .Take(endIndex - startIndex + 1)
                .ToList();

            var totalTokens = EstimateMessagesTokens(messages);

            var hasReasoning = messages.Any(m => !string.IsNullOrWhiteSpace(m.ReasoningContent));

            var sample = new TrainingSample
            {
                Messages = ConvertMessages(messages),
                Metadata = new TrainingMetadata
                {
                    Source = "overflow-recovery",
                    SessionId = session.Id,
                    ExtractedAt = DateTime.UtcNow,
                    HasCorrection = false,
                    Model = session.ModelName,
                    ToolCallCount = messages.Count(m => m.Role == MessageRole.Assistant),
                    TotalTokens = totalTokens,
                    Score = 0.7, // 基础分：溢出恢复比修正轨迹略低，但仍然有价值
                    HasReasoning = hasReasoning,
                    ReasoningChainLength = CountReasoningChain(messages)
                }
            };

            samples.Add(sample);
        }

        return samples;
    }

    /// <summary>
    /// 从 session 中提取 DPO 偏好对（chosen/rejected）。
    /// 每条修正轨迹可生成一个偏好对：
    /// - prompt: 从轮次开始到失败 assistant 消息之前
    /// - rejected: 失败的 assistant 消息 + 错误结果
    /// - chosen: 修正后的 assistant 消息 + 成功结果
    /// </summary>
    public static IReadOnlyList<DpoPreferenceSample> ExtractPreferencePairs(
        AgentSession session)
    {
        var trajectories = CorrectionDetector.Detect(session.Messages);
        var samples = new List<DpoPreferenceSample>();

        foreach (var traj in trajectories)
        {
            // 1. Prompt: 从 turn 开始到失败 assistant 消息之前
            var promptStart = FindTurnStartIndex(session.Messages, traj.FailureMessageIndex);
            var promptMessages = session.Messages
                .Skip(promptStart)
                .Take(traj.FailureMessageIndex - promptStart)
                .ToList();

            // 2. Rejected: 失败的 assistant 消息 + 错误 tool result
            var rejectedAssistant = ConvertSingleMessage(session.Messages[traj.FailureMessageIndex]);
            var errorResult = FindToolResultById(session.Messages, traj.FailedToolCall.Id);
            var rejectedMessages = new List<TrainingMessage>();
            if (rejectedAssistant is not null)
                rejectedMessages.Add(rejectedAssistant);
            if (errorResult is not null)
                rejectedMessages.Add(errorResult);

            // 3. Chosen: 修正后的 assistant 消息 + 成功 tool result
            var successMsgIndex = FindAssistantWithToolCall(session.Messages, traj.SuccessfulToolCall.Id);
            ChatMessage? successMsg = null;
            if (successMsgIndex >= 0)
                successMsg = session.Messages[successMsgIndex];

            var chosenMessages = new List<TrainingMessage>();
            TrainingMessage? chosenAssistant = null;
            if (successMsg is not null)
            {
                chosenAssistant = ConvertSingleMessage(successMsg);
                if (chosenAssistant is not null)
                    chosenMessages.Add(chosenAssistant);
            }
            var successResult = FindToolResultById(session.Messages, traj.SuccessfulToolCall.Id);
            if (successResult is not null)
                chosenMessages.Add(successResult);

            if (promptMessages.Count == 0 || rejectedMessages.Count == 0 || chosenMessages.Count == 0)
                continue;

            var errorSummary = Truncate(
                FindToolResultContent(session.Messages, traj.FailedToolCall.Id), 200);

            var hasReasoning = !string.IsNullOrWhiteSpace(traj.FailureAssistantMessage.ReasoningContent)
                || (successMsg?.ReasoningContent is not null);

            var pair = new DpoPreferenceSample
            {
                Prompt = ConvertMessages(promptMessages),
                Chosen = chosenMessages,
                Rejected = rejectedMessages,
                Metadata = new DpoMetadata
                {
                    Source = "agent-correction-dpo",
                    SessionId = session.Id,
                    ExtractedAt = DateTime.UtcNow,
                    Model = session.ModelName,
                    FailedToolName = traj.FailedToolCall.Name,
                    SuccessToolName = traj.SuccessfulToolCall.Name,
                    ErrorSummary = errorSummary,
                    HasReasoning = hasReasoning
                }
            };

            samples.Add(pair);
        }

        return samples;
    }

    /// <summary>
    /// 提取完整会话作为训练样本（适用于预训练/全量 SFT）
    /// </summary>
    public static TrainingSample ExtractFullSession(
        AgentSession session,
        int totalTokens)
    {
        return new TrainingSample
        {
            Messages = ConvertMessages(session.Messages.ToList()),
            Metadata = new TrainingMetadata
            {
                Source = "agent-full-session",
                SessionId = session.Id,
                ExtractedAt = DateTime.UtcNow,
                HasCorrection = false,
                Model = session.ModelName,
                ToolCallCount = session.Messages.Count(m => m.Role == MessageRole.Assistant),
                TotalTokens = totalTokens,
                Score = 1.0
            }
        };
    }

    // --- 内部转换方法 ---

    private static List<TrainingMessage> ConvertMessages(List<ChatMessage> messages)
    {
        var result = new List<TrainingMessage>();
        foreach (var msg in messages)
        {
            var role = ConvertRole(msg.Role);
            if (role is null) continue; // 跳过 Compaction, System 等内部角色

            var trainingMsg = new TrainingMessage
            {
                Role = role,
                Content = msg.Content
            };

            // 处理 tool calls（assistant 发起）
            if (role == "assistant")
            {
                // 推理链
                if (!string.IsNullOrWhiteSpace(msg.ReasoningContent))
                {
                    trainingMsg.Reasoning = msg.ReasoningContent;
                }

                var toolCalls = AssistantToolCallsCodec.Deserialize(msg.ToolCallsJson);
                if (toolCalls?.Count > 0)
                {
                    trainingMsg.ToolCalls = toolCalls
                        .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                        .Select(tc => new TrainingToolCall
                        {
                            Id = tc.Id,
                            Function = new TrainingFunction
                            {
                                Name = tc.Name,
                                Arguments = JsonSerializer.Serialize(tc.Arguments)
                            }
                        })
                        .ToList();
                }
            }

            // 处理 tool_call_id（tool 角色回复）
            if (role == "tool")
            {
                var toolCallId = ModelMessageBuilder.ExtractToolCallId(msg.Content);
                if (!string.IsNullOrWhiteSpace(toolCallId))
                {
                    trainingMsg.ToolCallId = toolCallId;
                }
            }

            result.Add(trainingMsg);
        }
        return result;
    }

    private static string? ConvertRole(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        _ => null
    };

    private static int FindTurnStartIndex(IReadOnlyList<ChatMessage> messages, int beforeIndex)
    {
        for (var i = beforeIndex; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.User)
                return i;
        }
        return 0;
    }

    private static int FindTrajectoryEndIndex(IReadOnlyList<ChatMessage> messages, AgentToolCall successCall)
    {
        // 找到包含成功 tool call 的 assistant 消息的下一条消息的索引
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.Assistant)
            {
                var toolCalls = AssistantToolCallsCodec.Deserialize(messages[i].ToolCallsJson);
                if (toolCalls?.Any(tc => tc.Id == successCall.Id) == true)
                {
                    // 返回包含成功调用的 assistant 消息索引
                    // 如果后面还有 tool result，一并包含进去
                    var endIndex = i;
                    for (var j = i + 1; j < messages.Count; j++)
                    {
                        if (messages[j].Role == MessageRole.Tool)
                        {
                            endIndex = j;
                        }
                        else
                        {
                            break;
                        }
                    }
                    return endIndex;
                }
            }
        }
        return messages.Count - 1;
    }

    /// <summary>
    /// 计算修正轨迹的质量分数（0.0 ~ 1.0）。
    /// 分数越高，表示这条修正轨迹越"有价值"用于训练。
    /// </summary>
    private static double ComputeQualityScore(CorrectionDetector.CorrectionTrajectory traj)
    {
        var score = 0.5; // base

        // 加分：修正指令有一定长度（说明用户给了足够的信息）
        var correctionLen = traj.CorrectionMessage.Content?.Length ?? 0;
        if (correctionLen > 20)
            score += 0.15;
        if (correctionLen > 100)
            score += 0.1;

        // 加分：失败的 tool call 有一定的参数复杂度
        if (traj.FailedToolCall.Arguments.Count > 0)
            score += 0.1;
        if (traj.FailedToolCall.Arguments.Count > 2)
            score += 0.1;

        // 加分：失败消息有推理链（说明模型"思考过"错误，蒸馏价值更高）
        if (!string.IsNullOrWhiteSpace(traj.FailureAssistantMessage.ReasoningContent))
            score += 0.15;

        // 上限 1.0
        return Math.Min(1.0, score);
    }

    private static int EstimateMessagesTokens(List<ChatMessage> messages)
    {
        return ContextTokenEstimator.Estimate(messages, includeReasoningInModelContext: true);
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxChars
            ? value
            : value[..maxChars] + "...";
    }

    private static int FindMessageIndex(IReadOnlyList<ChatMessage> messages, string messageId)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == messageId)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 将单条 ChatMessage 转换为 TrainingMessage（没有 tool_calls 时返回 null）。
    /// </summary>
    private static TrainingMessage? ConvertSingleMessage(ChatMessage msg)
    {
        var role = ConvertRole(msg.Role);
        if (role is null)
            return null;

        var result = new TrainingMessage
        {
            Role = role,
            Content = msg.Content,
            Reasoning = msg.ReasoningContent
        };

        if (role == "assistant")
        {
            var toolCalls = AssistantToolCallsCodec.Deserialize(msg.ToolCallsJson);
            if (toolCalls?.Count > 0)
            {
                result.ToolCalls = toolCalls
                    .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                    .Select(tc => new TrainingToolCall
                    {
                        Id = tc.Id,
                        Function = new TrainingFunction
                        {
                            Name = tc.Name,
                            Arguments = JsonSerializer.Serialize(tc.Arguments)
                        }
                    })
                    .ToList();
            }
        }

        if (role == "tool")
        {
            var toolCallId = ModelMessageBuilder.ExtractToolCallId(msg.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
                result.ToolCallId = toolCallId;
        }

        return result;
    }

    /// <summary>
    /// 根据 tool call ID 查找对应的 tool result 消息。
    /// </summary>
    private static TrainingMessage? FindToolResultById(IReadOnlyList<ChatMessage> messages, string toolCallId)
    {
        foreach (var msg in messages)
        {
            if (msg.Role != MessageRole.Tool)
                continue;

            var tId = ModelMessageBuilder.ExtractToolCallId(msg.Content);
            if (tId == toolCallId)
            {
                return ConvertSingleMessage(msg);
            }
        }
        return null;
    }

    /// <summary>
    /// 找到包含指定 tool call ID 的 assistant 消息索引。
    /// </summary>
    private static int FindAssistantWithToolCall(IReadOnlyList<ChatMessage> messages, string toolCallId)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != MessageRole.Assistant)
                continue;

            var toolCalls = AssistantToolCallsCodec.Deserialize(messages[i].ToolCallsJson);
            if (toolCalls?.Any(tc => tc.Id == toolCallId) == true)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 获取指定 tool call ID 的 result 内容。
    /// </summary>
    private static string? FindToolResultContent(IReadOnlyList<ChatMessage> messages, string toolCallId)
    {
        foreach (var msg in messages)
        {
            if (msg.Role != MessageRole.Tool)
                continue;

            var tId = ModelMessageBuilder.ExtractToolCallId(msg.Content);
            if (tId == toolCallId)
                return msg.Content;
        }
        return null;
    }

    /// <summary>
    /// 统计连续 assistant 消息中带有 ReasoningContent 的最大长度。
    /// 例如 3 条连续的带有推理的 assistant 消息 → 返回 3。
    /// </summary>
    private static int CountReasoningChain(List<ChatMessage> messages)
    {
        var maxChain = 0;
        var currentChain = 0;
        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(msg.ReasoningContent))
            {
                currentChain++;
                if (currentChain > maxChain) maxChain = currentChain;
            }
            else
            {
                currentChain = 0;
            }
        }
        return maxChain;
    }
}
