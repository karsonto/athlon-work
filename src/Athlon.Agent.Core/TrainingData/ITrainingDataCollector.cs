namespace Athlon.Agent.Core.TrainingData;

/// <summary>
/// 训练数据采集器接口。由 AgentRuntime 在关键点位调用。
/// </summary>
public interface ITrainingDataCollector
{
    /// <summary>
    /// 保存一轮完整对话回合的轨迹数据。
    /// 在每次 agent 完成一轮对话（从用户输入到最终回复）后调用。
    /// </summary>
    Task RecordTurnAsync(AgentSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存完整会话为一条训练样本。
    /// 在会话结束时调用。
    /// </summary>
    Task RecordSessionAsync(AgentSession session, int totalTokens, CancellationToken cancellationToken = default);
}
