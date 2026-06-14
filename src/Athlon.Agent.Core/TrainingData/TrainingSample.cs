using System.Text.Json.Serialization;

namespace Athlon.Agent.Core.TrainingData;

/// <summary>
/// 一条训练样本 —— 直接对应 HuggingFace Datasets 的 messages 格式。
/// 序列化后即可被 datasets.load_dataset("json") 消费。
/// </summary>
public sealed class TrainingSample
{
    /// <summary>
    /// 对话消息列表（HuggingFace 格式）。
    /// role: system / user / assistant / tool
    /// </summary>
    [JsonPropertyName("messages")]
    public required List<TrainingMessage> Messages { get; set; }

    /// <summary>
    /// 样本元数据（不参与训练，用于筛选和溯源）。
    /// </summary>
    [JsonPropertyName("metadata")]
    public required TrainingMetadata Metadata { get; set; }
}

public sealed class TrainingMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>
    /// Tool calls（assistant 发出）。格式对齐 OpenAI tool_calls 规范。
    /// </summary>
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TrainingToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Tool call ID（tool 角色的回复关联）。
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// 模型的思考链/推理过程（仅 assistant 角色有）。
    /// 用于 Chain-of-Thought 蒸馏。
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reasoning { get; set; }
}

public sealed class TrainingToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>
    /// 类型，固定为 "function"。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required TrainingFunction Function { get; set; }
}

public sealed class TrainingFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// 参数的 JSON 字符串。
    /// </summary>
    [JsonPropertyName("arguments")]
    public required string Arguments { get; set; }
}

/// <summary>
/// DPO 偏好样本（chosen/rejected 对）。
/// 序列化后可直接被 TRL 的 DPOTrainer 消费。
/// </summary>
public sealed class DpoPreferenceSample
{
    /// <summary>
    /// 提示上下文（system + 多轮对话 + 用户请求，到失败的 assistant 消息之前）。
    /// </summary>
    [JsonPropertyName("prompt")]
    public required List<TrainingMessage> Prompt { get; set; }

    /// <summary>
    /// 期望的（修正后的）助手回复 + tool result。
    /// </summary>
    [JsonPropertyName("chosen")]
    public required List<TrainingMessage> Chosen { get; set; }

    /// <summary>
    /// 不期望的（失败的）助手回复 + 错误结果。
    /// </summary>
    [JsonPropertyName("rejected")]
    public required List<TrainingMessage> Rejected { get; set; }

    /// <summary>
    /// 样本元数据。
    /// </summary>
    [JsonPropertyName("metadata")]
    public required DpoMetadata Metadata { get; set; }
}

public sealed class DpoMetadata
{
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("extractedAt")]
    public DateTime ExtractedAt { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("failedToolName")]
    public string? FailedToolName { get; set; }

    [JsonPropertyName("successToolName")]
    public string? SuccessToolName { get; set; }

    [JsonPropertyName("errorSummary")]
    public string? ErrorSummary { get; set; }

    [JsonPropertyName("hasReasoning")]
    public bool HasReasoning { get; set; }
}

public sealed class TrainingMetadata
{
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("extractedAt")]
    public DateTime ExtractedAt { get; set; }

    [JsonPropertyName("hasCorrection")]
    public bool HasCorrection { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("toolCallCount")]
    public int ToolCallCount { get; set; }

    [JsonPropertyName("totalTokens")]
    public int TotalTokens { get; set; }

    /// <summary>
    /// 质量分数（0.0 ~ 1.0），可用于 GRPO 的 reward 或 preference 排序。
    /// </summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("failedToolCallIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FailedToolCallIds { get; set; }

    [JsonPropertyName("correctionSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrectionSummary { get; set; }

    /// <summary>
    /// 是否包含模型的思考链/推理内容。
    /// 可用于筛选出适合做 CoT 蒸馏的样本。
    /// </summary>
    [JsonPropertyName("hasReasoning")]
    public bool HasReasoning { get; set; }

    /// <summary>
    /// 包含推理链的连续 assistant 消息数量（越长的推理链蒸馏价值越高）。
    /// </summary>
    [JsonPropertyName("reasoningChainLength")]
    public int ReasoningChainLength { get; set; }
}
