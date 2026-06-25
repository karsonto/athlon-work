using System.Text.Json;

namespace Athlon.Agent.Core.TrainingData;

/// <summary>
/// 默认训练数据采集器实现。
/// 将修正轨迹和完整会话输出为 HuggingFace 兼容的 JSON Lines 文件。
/// 默认禁用，需通过 settings.json 启用。
/// </summary>
public sealed class TrainingSampleStore : ITrainingDataCollector, IDisposable
{
    private readonly TrainingDataSettings _settings;
    private readonly IAppLogger _logger;
    private readonly string _outputDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private StreamWriter? _sftWriter;
    private StreamWriter? _dpoWriter;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TrainingSampleStore(TrainingDataSettings settings, IAppLogger logger)
    {
        _settings = settings;
        _logger = logger.ForContext("TrainingSampleStore");
        _outputDir = ResolveOutputDirectory(settings.OutputDirectory);
        Directory.CreateDirectory(_outputDir);
    }

    public async Task RecordTurnAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return;

        // 采样：sampleRate < 1.0 时按比例随机保存
        if (_settings.SampleRate < 1.0 && Random.Shared.NextDouble() > _settings.SampleRate)
            return;

        try
        {
            // 修正轨迹
            var correctionSamples = TurnTrajectoryExtractor.ExtractCorrectionSamples(session);
            foreach (var sample in correctionSamples)
            {
                await WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
            }

            // 超时/溢出恢复轨迹
            var overflowSamples = TurnTrajectoryExtractor.ExtractOverflowRecoverySamples(session);
            foreach (var sample in overflowSamples)
            {
                await WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
            }

            // DPO 偏好对（从修正轨迹生成 chosen/rejected）
            var dpoPairs = TurnTrajectoryExtractor.ExtractPreferencePairs(session);
            foreach (var pair in dpoPairs)
            {
                await WriteDpoSampleAsync(pair, cancellationToken).ConfigureAwait(false);
            }

            var totalCount = correctionSamples.Count + overflowSamples.Count + dpoPairs.Count;
            if (totalCount == 0)
                return;

            _logger.Information("Saved {Count} training samples ({Correction} correction + {Overflow} overflow-recovery + {Dpo} dpo) for session {SessionId}",
                totalCount, correctionSamples.Count, overflowSamples.Count, dpoPairs.Count, session.Id);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data extraction failed: {Error}", ex.Message);
        }
    }

    public async Task RecordSessionAsync(AgentSession session, int totalTokens, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return;

        if (_settings.SampleRate < 1.0 && Random.Shared.NextDouble() > _settings.SampleRate)
            return;

        try
        {
            var sample = TurnTrajectoryExtractor.ExtractFullSession(session, totalTokens);
            await WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);

            _logger.Information("Saved full session sample for session {SessionId} ({Tokens} tokens)",
                session.Id, totalTokens);
        }
        catch (Exception ex)
        {
            _logger.Warning("Training data extraction failed: {Error}", ex.Message);
        }
    }

    private async Task WriteSampleAsync(TrainingSample sample, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(sample, JsonOptions);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = GetSftWriter();
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteDpoSampleAsync(DpoPreferenceSample sample, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(sample, JsonOptions);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = GetDpoWriter();
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private StreamWriter GetSftWriter()
    {
        if (_sftWriter is not null)
            return _sftWriter;

        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_outputDir, $"sft-traces-{date}.jsonl");
        _sftWriter = new StreamWriter(filePath, append: true, encoding: new System.Text.UTF8Encoding(false));
        return _sftWriter;
    }

    private StreamWriter GetDpoWriter()
    {
        if (_dpoWriter is not null)
            return _dpoWriter;

        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_outputDir, $"dpo-preference-{date}.jsonl");
        _dpoWriter = new StreamWriter(filePath, append: true, encoding: new System.Text.UTF8Encoding(false));
        return _dpoWriter;
    }

    private static string ResolveOutputDirectory(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, ".athlon-agent", "training-data");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.Wait();
        try
        {
            _sftWriter?.Dispose();
            _sftWriter = null;
            _dpoWriter?.Dispose();
            _dpoWriter = null;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}

/// <summary>
/// 训练数据采集配置。
/// </summary>
public sealed class TrainingDataSettings
{
    /// <summary>是否启用训练数据采集（默认关闭）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>输出目录（空时默认 ~/.athlon-agent/training-data/）</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>采样率 0.0~1.0（生产环境建议 0.1）</summary>
    public double SampleRate { get; set; } = 1.0;
}
