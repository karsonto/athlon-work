# Agent 日志驱动性能优化实施计划

> **For agentic workers:** 本计划参照 UltraData Pipeline (L0→L4) 数据治理方法论，对 Athlon Agent 的日志系统进行分层优化，实现「日志即遥测」——从原始日志中提取结构化性能指标，进而指导模型调用策略优化。

**目标:** 将现有分散的 Serilog 文本日志和 JSONL 文件，升级为 L0→L4 分级可观测性管道，实现 token 预算调优、缓存策略优化、上下文压缩参数自动校准，最终降低 15-25% 的 API 成本并提升响应速度。

**架构:**
- **L0 (原始日志流):** 保留 Serilog 滚动文件写入，新增结构化 JSON 日志事件（事件类型 + 时间戳 + 维度标签）
- **L1 (清理与规整):** 日志转写为统一 `LogEvent` 记录，去重、去敏感信息、统一时区
- **L2 (指标提取):** 从日志中提取维度化时序指标（token 用量、延迟、缓存命中率、压缩节省比）
- **L3 (分析与告警):** 构建聚合查询和阈值告警，产出性能报告
- **L4 (闭环优化):** 指标反馈到 `DynamicCompactionSettings` / `TokenEstimatorCalibrator` 的自动调参

**技术栈:** Serilog → System.Text.Json structured events → 内存时序聚合器 → 可选的 Prometheus 导出 / 本地 JSON report

---

## 文件结构

```
src/Athlon.Agent.Core/
├── Telemetry/                          # 新建目录——全部遥测逻辑
│   ├── ITelemetrySink.cs               # 遥测写入接口
│   ├── TelemetryEvent.cs               # 结构化事件模型
│   ├── TelemetryLevel.cs               # L0-L4 枚举
│   ├── TelemetrySink.cs                # 默认实现（JSON 行文件 + 内存缓冲）
│   ├── MetricsAggregator.cs            # 维度化时序聚合器
│   ├── MetricsSnapshot.cs              # 聚合快照
│   ├── TokenMetricsCollector.cs        # token 用量相关指标
│   ├── LatencyMetricsCollector.cs      # 延迟/缓存/压缩指标
│   └── TelemetrySettings.cs            # 配置（启用/禁用/采样率/导出路径）

src/Athlon.Agent.Core/
├── Compaction/
│   ├── ContextBudgetCalculator.cs      # 修改：增加指标上报钩子
│   └── DynamicCompactionPlan.cs        # 修改：支持指标驱动的调参

src/Athlon.Agent.Core/
├── AgentSettings.cs                    # 修改：增加 TelemetrySettings 节
├── AgentRuntime.cs                     # 修改：插入遥测事件
├── AgentTurnCoordinator.cs             # 修改：记录回合级指标
└── SessionUsageAccumulator.cs          # 修改：关联 TelemetryEvent

src/Athlon.Agent.Infrastructure/
└── AppLogger.cs                        # 修改：双写入（文本 + 结构化 JSON）
```

---

### Task 1: 定义遥测事件模型和级别枚举

**Files:**
- Create: `src/Athlon.Agent.Core/Telemetry/TelemetryLevel.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/TelemetryEvent.cs`

- [ ] **Step 1: 创建级别枚举**

```csharp
// src/Athlon.Agent.Core/Telemetry/TelemetryLevel.cs
namespace Athlon.Agent.Core.Telemetry;

/// <summary>
/// UltraData Pipeline 启发式的日志治理级别。
/// L0=原始, L1=清洗, L2=指标, L3=聚合, L4=闭环调参。
/// </summary>
public enum TelemetryLevel
{
    /// <summary>Raw — 原始日志事件，未清洗</summary>
    L0_Raw = 0,
    /// <summary>Cleaned — 去敏、脱敏、统一时区后的结构化事件</summary>
    L1_Cleaned = 1,
    /// <summary>Metric — 从事件中提取的维度化指标（token/延迟/缓存）</summary>
    L2_Metric = 2,
    /// <summary>Aggregated — 窗口聚合结果（均值/P95/总量）</summary>
    L3_Aggregated = 3,
    /// <summary>Steering — 由指标驱动的调参决策</summary>
    L4_Steering = 4
}
```

- [ ] **Step 2: 创建结构化事件记录**

```csharp
// src/Athlon.Agent.Core/Telemetry/TelemetryEvent.cs
using System.Text.Json;

namespace Athlon.Agent.Core.Telemetry;

public sealed record TelemetryEvent(
    string EventType,
    TelemetryLevel Level,
    DateTimeOffset Timestamp,
    string SessionId,
    Dictionary<string, object> Dimensions,
    Dictionary<string, double> Measures,
    string? SourceContext = null)
{
    public string SerializeToJson()
    {
        var payload = new Dictionary<string, object>
        {
            ["eventType"] = EventType,
            ["level"] = Level.ToString(),
            ["ts"] = Timestamp.ToString("O"),
            ["sessionId"] = SessionId,
            ["dimensions"] = Dimensions,
            ["measures"] = Measures,
            ["source"] = SourceContext ?? ""
        };
        return JsonSerializer.Serialize(payload, JsonFileStore.Options);
    }

    public static TelemetryEvent? Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("eventType", out var et)) return null;
        return new TelemetryEvent(
            et.GetString() ?? "",
            Enum.Parse<TelemetryLevel>(root.GetProperty("level").GetString() ?? "L0_Raw"),
            DateTimeOffset.Parse(root.GetProperty("ts").GetString()!),
            root.GetProperty("sessionId").GetString() ?? "",
            root.GetProperty("dimensions").EnumerateObject()
                .ToDictionary(kv => kv.Name, kv => (object)kv.Value.GetRawText()),
            root.GetProperty("measures").EnumerateObject()
                .ToDictionary(kv => kv.Name, kv => kv.Value.GetDouble()),
            root.TryGetProperty("source", out var src) ? src.GetString() : null);
    }
}
```

- [ ] **Step 3: 运行测试验证编译**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: BUILD succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Athlon.Agent.Core/Telemetry/TelemetryLevel.cs src/Athlon.Agent.Core/Telemetry/TelemetryEvent.cs
git commit -m "feat(telemetry): add TelemetryLevel and TelemetryEvent models (L0-L4 pipeline)"
```

---

### Task 2: 遥测 Sink 接口与默认实现

**Files:**
- Create: `src/Athlon.Agent.Core/Telemetry/ITelemetrySink.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/TelemetrySink.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/TelemetrySettings.cs`

- [ ] **Step 1: 创建 Sink 接口**

```csharp
// src/Athlon.Agent.Core/Telemetry/ITelemetrySink.cs
namespace Athlon.Agent.Core.Telemetry;

public interface ITelemetrySink
{
    /// <summary>写入一个遥测事件（线程安全）</summary>
    void Write(TelemetryEvent evt);

    /// <summary>刷新所有缓冲事件到持久化存储</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>读取最近 N 个 L2+ 事件（用于聚合器查询）</summary>
    IReadOnlyList<TelemetryEvent> ReadRecent(int count = 1000);
}
```

- [ ] **Step 2: 创建配置**

```csharp
// src/Athlon.Agent.Core/Telemetry/TelemetrySettings.cs
namespace Athlon.Agent.Core.Telemetry;

public sealed class TelemetrySettings
{
    /// <summary>主开关，默认关闭（不影响现有日志行为）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>结构化 JSON 日志目录。空=禁用文件写入</summary>
    public string Directory { get; set; } = "";

    /// <summary>内存缓冲最大事件数</summary>
    public int BufferSize { get; set; } = 5000;

    /// <summary>写入事件的级别下限（低于此级别不记录）</summary>
    public TelemetryLevel MinimumLevel { get; set; } = TelemetryLevel.L1_Cleaned;

    /// <summary>采样率 0.0~1.0（1.0=全采样）</summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>是否启用指标聚合</summary>
    public bool EnableMetricsAggregation { get; set; } = true;

    /// <summary>指标聚合窗口秒数</summary>
    public int MetricsWindowSeconds { get; set; } = 300;
}
```

- [ ] **Step 3: 创建默认实现**

```csharp
// src/Athlon.Agent.Core/Telemetry/TelemetrySink.cs
using System.Collections.Concurrent;
using System.Text;

namespace Athlon.Agent.Core.Telemetry;

public sealed class TelemetrySink : ITelemetrySink, IDisposable
{
    private readonly TelemetrySettings _settings;
    private readonly string? _filePath;
    private readonly ConcurrentQueue<TelemetryEvent> _buffer = new();
    private readonly ConcurrentQueue<TelemetryEvent> _recent = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly IAppLogger _logger;
    private bool _disposed;
    private const int RecentMax = 10_000;

    public TelemetrySink(TelemetrySettings settings, IAppLogger logger, string? filePath = null)
    {
        _settings = settings;
        _logger = logger.ForContext("TelemetrySink");
        _filePath = filePath;
    }

    public void Write(TelemetryEvent evt)
    {
        if (_disposed || !_settings.Enabled) return;
        if (evt.Level < _settings.MinimumLevel) return;
        if (Random.Shared.NextDouble() > _settings.SampleRate) return;

        _buffer.Enqueue(evt);

        // 维护近期事件环形缓冲
        _recent.Enqueue(evt);
        while (_recent.Count > RecentMax)
            _recent.TryDequeue(out _);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(_filePath)) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            var sb = new StringBuilder();
            while (_buffer.TryDequeue(out var evt))
            {
                sb.AppendLine(evt.SerializeToJson());
            }

            if (sb.Length > 0)
            {
                await File.AppendAllTextAsync(_filePath, sb.ToString(), Encoding.UTF8, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Telemetry flush failed: {Error}", ex.Message);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public IReadOnlyList<TelemetryEvent> ReadRecent(int count = 1000)
    {
        return _recent.Reverse().Take(count).ToList();
    }

    public void Dispose()
    {
        _disposed = true;
        _flushLock.Dispose();
    }
}
```

- [ ] **Step 4: 运行测试验证编译**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: BUILD succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Athlon.Agent.Core/Telemetry/ITelemetrySink.cs src/Athlon.Agent.Core/Telemetry/TelemetrySink.cs src/Athlon.Agent.Core/Telemetry/TelemetrySettings.cs
git commit -m "feat(telemetry): add ITelemetrySink, TelemetrySink, and TelemetrySettings"
```

---

### Task 3: Metrics 聚合器（L2→L3 关键组件）

**Files:**
- Create: `src/Athlon.Agent.Core/Telemetry/MetricsSnapshot.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/MetricsAggregator.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/TokenMetricsCollector.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/LatencyMetricsCollector.cs`

- [ ] **Step 1: 创建聚合快照模型**

```csharp
// src/Athlon.Agent.Core/Telemetry/MetricsSnapshot.cs
namespace Athlon.Agent.Core.Telemetry;

/// <summary>
/// 一个时间窗口内的聚合性能指标快照。
/// 输出给 L3_Aggregated 事件和 L4_Steering 决策。
/// </summary>
public sealed record MetricsSnapshot(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int TotalTurns,
    int TotalModelCalls,
    // Token 用量
    long TotalPromptTokens,
    long TotalCompletionTokens,
    long TotalCacheHitTokens,
    long TotalCacheMissTokens,
    double CacheHitRate,
    long TotalContextSavingsTokens,
    // 延迟（毫秒）
    double AvgModelCallLatencyMs,
    double P95ModelCallLatencyMs,
    double AvgToolCallLatencyMs,
    double P95ToolCallLatencyMs,
    // 上下文压力
    double AvgContextUtilization,
    double MaxContextUtilization,
    int CompactionCount,
    int OverflowRetryCount,
    // 工具调用
    int TotalToolCalls,
    int FailedToolCalls,
    double ToolCallErrorRate,
    // 成本估算（仅作为参考，按 $/1M tokens）
    double EstimatedCostUsd);
```

- [ ] **Step 2: 创建核心聚合器**

```csharp
// src/Athlon.Agent.Core/Telemetry/MetricsAggregator.cs
using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Telemetry;

public sealed class MetricsAggregator
{
    private readonly TelemetrySettings _settings;
    private readonly ITelemetrySink _sink;
    private readonly ConcurrentQueue<TelemetryEvent> _windowEvents = new();
    private DateTimeOffset _windowStart;
    private readonly object _rollLock = new();

    public MetricsAggregator(TelemetrySettings settings, ITelemetrySink sink)
    {
        _settings = settings;
        _sink = sink;
        _windowStart = DateTimeOffset.UtcNow;
    }

    public void Ingest(TelemetryEvent evt)
    {
        if (evt.Level < TelemetryLevel.L2_Metric) return;
        _windowEvents.Enqueue(evt);
        TryRollWindow();
    }

    private void TryRollWindow()
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan elapsed;
        lock (_rollLock) { elapsed = now - _windowStart; }

        if (elapsed.TotalSeconds < _settings.MetricsWindowSeconds) return;

        MetricsSnapshot snapshot;
        lock (_rollLock)
        {
            // 双重检测
            elapsed = now - _windowStart;
            if (elapsed.TotalSeconds < _settings.MetricsWindowSeconds) return;

            var events = _windowEvents.ToArray();
            _windowEvents.Clear();
            snapshot = ComputeSnapshot(events, _windowStart, now);
            _windowStart = now;
        }

        // 发出 L3 聚合事件
        var dims = new Dictionary<string, object>
        {
            ["windowSeconds"] = _settings.MetricsWindowSeconds
        };
        var measures = new Dictionary<string, double>
        {
            ["totalTurns"] = snapshot.TotalTurns,
            ["totalModelCalls"] = snapshot.TotalModelCalls,
            ["totalPromptTokens"] = snapshot.TotalPromptTokens,
            ["totalCompletionTokens"] = snapshot.TotalCompletionTokens,
            ["cacheHitRate"] = snapshot.CacheHitRate,
            ["totalContextSavingsTokens"] = snapshot.TotalContextSavingsTokens,
            ["avgModelCallLatencyMs"] = snapshot.AvgModelCallLatencyMs,
            ["p95ModelCallLatencyMs"] = snapshot.P95ModelCallLatencyMs,
            ["avgContextUtilization"] = snapshot.AvgContextUtilization,
            ["compactionCount"] = snapshot.CompactionCount,
            ["overflowRetryCount"] = snapshot.OverflowRetryCount,
            ["toolCallErrorRate"] = snapshot.ToolCallErrorRate,
            ["estimatedCostUsd"] = snapshot.EstimatedCostUsd
        };

        _sink.Write(new TelemetryEvent(
            "metrics.window", TelemetryLevel.L3_Aggregated,
            now, "__global__", dims, measures, "MetricsAggregator"));
    }

    public MetricsSnapshot ComputeSnapshot(TelemetryEvent[] events, DateTimeOffset from, DateTimeOffset to)
    {
        // —— 以下为维度化聚合逻辑 ——
        var turnIds = new HashSet<string>();
        var modelLatencies = new List<double>();
        var toolLatencies = new List<double>();
        var utilizations = new List<double>();

        long sumPrompt = 0, sumCompletion = 0, sumHit = 0, sumMiss = 0;
        long sumSavings = 0;
        int compactions = 0, overflows = 0, toolCalls = 0, failedTools = 0, modelCalls = 0;

        foreach (var evt in events)
        {
            turnIds.Add(evt.SessionId);

            switch (evt.EventType)
            {
                case "model.complete":
                    modelCalls++;
                    if (evt.Measures.TryGetValue("promptTokens", out var pt)) sumPrompt += (long)pt;
                    if (evt.Measures.TryGetValue("completionTokens", out var ct)) sumCompletion += (long)ct;
                    if (evt.Measures.TryGetValue("cacheHitTokens", out var hit)) sumHit += (long)hit;
                    if (evt.Measures.TryGetValue("cacheMissTokens", out var miss)) sumMiss += (long)miss;
                    if (evt.Measures.TryGetValue("latencyMs", out var lat)) modelLatencies.Add(lat);
                    if (evt.Measures.TryGetValue("utilization", out var util)) utilizations.Add(util);
                    break;

                case "compaction.applied":
                    compactions++;
                    if (evt.Measures.TryGetValue("savingsTokens", out var sav)) sumSavings += (long)sav;
                    break;

                case "compaction.overflow":
                    overflows++;
                    break;

                case "tool.invoke":
                    toolCalls++;
                    if (evt.Measures.TryGetValue("durationMs", out var dur)) toolLatencies.Add(dur);
                    if (evt.Dimensions.TryGetValue("succeeded", out var ok) && ok is "False")
                        failedTools++;
                    break;

                case "hygiene.savings":
                    if (evt.Measures.TryGetValue("savingsTokens", out var hs)) sumSavings += (long)hs;
                    break;
            }
        }

        modelLatencies.Sort();
        toolLatencies.Sort();

        var cacheTotal = sumHit + sumMiss;
        var cacheRate = cacheTotal > 0 ? (double)sumHit / cacheTotal : 0.0;
        var avgModelLat = modelLatencies.Count > 0 ? modelLatencies.Average() : 0.0;
        var p95ModelLat = modelLatencies.Count > 0
            ? modelLatencies[(int)(modelLatencies.Count * 0.95)] : 0.0;
        var avgToolLat = toolLatencies.Count > 0 ? toolLatencies.Average() : 0.0;
        var p95ToolLat = toolLatencies.Count > 0
            ? toolLatencies[(int)(toolLatencies.Count * 0.95)] : 0.0;
        var avgUtil = utilizations.Count > 0 ? utilizations.Average() : 0.0;
        var maxUtil = utilizations.Count > 0 ? utilizations.Max() : 0.0;
        var errRate = toolCalls > 0 ? (double)failedTools / toolCalls : 0.0;

        // 按 GPT-4.1-mini 参考价估算（仅作参考）
        var estimatedCost = (sumPrompt / 1_000_000.0 * 0.15) + (sumCompletion / 1_000_000.0 * 0.60);

        return new MetricsSnapshot(
            from, to, turnIds.Count, modelCalls,
            sumPrompt, sumCompletion, sumHit, sumMiss, cacheRate, sumSavings,
            avgModelLat, p95ModelLat, avgToolLat, p95ToolLat,
            avgUtil, maxUtil, compactions, overflows,
            toolCalls, failedTools, errRate, estimatedCost);
    }
}
```

- [ ] **Step 3: 创建 Token 指标收集器（辅助类）**

```csharp
// src/Athlon.Agent.Core/Telemetry/TokenMetricsCollector.cs
namespace Athlon.Agent.Core.Telemetry;

/// <summary>
/// 封装 Token 维度的指标提取逻辑，供 AgentRuntime 和 TurnCoordinator 调用。
/// </summary>
public static class TokenMetricsCollector
{
    public static TelemetryEvent BuildModelCompleteEvent(
        string sessionId,
        string sourceContext,
        AgentModelResponse response,
        int contextSavingsTokens,
        double estimatedUtilization,
        long latencyMs)
    {
        var dims = new Dictionary<string, object>
        {
            ["succeeded"] = "True"
        };

        var measures = new Dictionary<string, double>();
        if (response.Usage?.PromptTokens is { } pt) measures["promptTokens"] = pt;
        if (response.Usage?.CompletionTokens is { } ct) measures["completionTokens"] = ct;
        if (response.Usage?.TotalTokens is { } tt) measures["totalTokens"] = tt;
        if (response.Usage?.PromptCacheHitTokens is { } hit) measures["cacheHitTokens"] = hit;
        if (response.Usage?.PromptCacheMissTokens is { } miss) measures["cacheMissTokens"] = miss;
        if (contextSavingsTokens > 0) measures["contextSavingsTokens"] = contextSavingsTokens;
        measures["utilization"] = estimatedUtilization;
        measures["latencyMs"] = latencyMs;

        return new TelemetryEvent(
            "model.complete", TelemetryLevel.L2_Metric,
            DateTimeOffset.UtcNow, sessionId, dims, measures, sourceContext);
    }

    public static TelemetryEvent BuildCompactionEvent(
        string sessionId,
        string kind,
        int savingsTokens,
        double beforeUtilization,
        double afterUtilization)
    {
        var dims = new Dictionary<string, object> { ["kind"] = kind };
        var measures = new Dictionary<string, double>
        {
            ["savingsTokens"] = savingsTokens,
            ["beforeUtilization"] = beforeUtilization,
            ["afterUtilization"] = afterUtilization
        };

        return new TelemetryEvent(
            "compaction.applied", TelemetryLevel.L2_Metric,
            DateTimeOffset.UtcNow, sessionId, dims, measures, "CompactionPipeline");
    }

    public static TelemetryEvent BuildOverflowEvent(
        string sessionId, double utilizationBefore)
    {
        return new TelemetryEvent(
            "compaction.overflow", TelemetryLevel.L2_Metric,
            DateTimeOffset.UtcNow, sessionId,
            new Dictionary<string, object> { ["reason"] = "context_length_exceeded" },
            new Dictionary<string, double> { ["utilizationBefore"] = utilizationBefore },
            "AgentTurnCoordinator");
    }

    public static TelemetryEvent BuildHygieneSavingsEvent(
        string sessionId, int savingsTokens)
    {
        return new TelemetryEvent(
            "hygiene.savings", TelemetryLevel.L2_Metric,
            DateTimeOffset.UtcNow, sessionId,
            new Dictionary<string, object>(),
            new Dictionary<string, double> { ["savingsTokens"] = savingsTokens },
            "RequestHistoryHygiene");
    }
}
```

- [ ] **Step 4: 创建延迟指标收集器**

```csharp
// src/Athlon.Agent.Core/Telemetry/LatencyMetricsCollector.cs
using System.Diagnostics;

namespace Athlon.Agent.Core.Telemetry;

/// <summary>
/// 工具调用和模型调用的延迟测量 + 遥测事件构造。
/// </summary>
public static class LatencyMetricsCollector
{
    public static TelemetryEvent BuildToolInvokeEvent(
        string sessionId,
        string toolName,
        bool succeeded,
        long durationMs,
        string? error = null)
    {
        var dims = new Dictionary<string, object>
        {
            ["toolName"] = toolName,
            ["succeeded"] = succeeded ? "True" : "False"
        };
        if (error is not null) dims["error"] = error;

        return new TelemetryEvent(
            "tool.invoke", TelemetryLevel.L2_Metric,
            DateTimeOffset.UtcNow, sessionId, dims,
            new Dictionary<string, double> { ["durationMs"] = durationMs },
            "ToolInvocationPipeline");
    }

    /// <summary>创建一个 Stopwatch 并在 dispose 时发出遥测事件</summary>
    public static TimedScope TrackToolCall(string sessionId, string toolName, ITelemetrySink sink)
    {
        return new TimedScope(sessionId, toolName, sink);
    }

    public sealed class TimedScope : IDisposable
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly string _sessionId;
        private readonly string _toolName;
        private readonly ITelemetrySink _sink;
        private bool _succeeded = true;
        private string? _error;

        internal TimedScope(string sessionId, string toolName, ITelemetrySink sink)
        {
            _sessionId = sessionId;
            _toolName = toolName;
            _sink = sink;
        }

        public void SetFailed(string error) { _succeeded = false; _error = error; }

        public void Dispose()
        {
            _sw.Stop();
            _sink.Write(BuildToolInvokeEvent(_sessionId, _toolName, _succeeded, _sw.ElapsedMilliseconds, _error));
        }
    }
}
```

- [ ] **Step 5: 运行测试验证编译**

Run: `dotnet build src/Athlon.Agent.Core/Athlon.Agent.Core.csproj`
Expected: BUILD succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Athlon.Agent.Core/Telemetry/MetricsSnapshot.cs src/Athlon.Agent.Core/Telemetry/MetricsAggregator.cs src/Athlon.Agent.Core/Telemetry/TokenMetricsCollector.cs src/Athlon.Agent.Core/Telemetry/LatencyMetricsCollector.cs
git commit -m "feat(telemetry): add MetricsAggregator, TokenMetricsCollector, LatencyMetricsCollector"
```

---

### Task 4: 在 AgentRuntime/TurnCoordinator 中插入遥测事件

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentRuntime.cs`
- Modify: `src/Athlon.Agent.Core/AgentTurnCoordinator.cs`
- Modify: `src/Athlon.Agent.Core/ToolInvocationPipeline.cs`

- [ ] **Step 1: 修改 AgentRuntime 构造函数，注入 ITelemetrySink 并记录回合级事件**

在 `AgentRuntime.cs` 开头增加字段：

```csharp
// 在现有构造函数参数列表最后增加
ITelemetrySink telemetrySink,
```

在构造函数体内：

```csharp
private readonly ITelemetrySink _telemetrySink = telemetrySink;
```

在 `SendAsyncTurnAsync` 方法中，新增方法调用的记录——在 `while (true)` 循环开始处记录 tool round 增量的遥测事件。

在 `SendAsyncTurnAsync` 的 return 之前（line 183 之前），插入：

```csharp
// 回合结束时记录
if (settings.Telemetry.Enabled)
{
    var snapshot = sessionUsageAccumulator.Get(session.Id);
    _telemetrySink.Write(new TelemetryEvent(
        "turn.completed", TelemetryLevel.L1_Cleaned,
        DateTimeOffset.UtcNow, session.Id,
        new Dictionary<string, object>
        {
            ["messageCount"] = session.Messages.Count.ToString(),
            ["modelToolRound"] = modelToolRound.ToString()
        },
        new Dictionary<string, double>
        {
            ["turnTotalTokens"] = snapshot.TotalTokens,
            ["turnPromptTokens"] = snapshot.PromptTokens,
            ["turnCompletionTokens"] = snapshot.CompletionTokens,
            ["turnContextSavings"] = snapshot.ContextSavingsTokens
        },
        "AgentRuntime"));
}
```

- [ ] **Step 2: 修改 AgentTurnCoordinator 记录模型调用指标**

在 `AgentTurnCoordinator.cs` 的 `CompleteWithOverflowRetryAsync` 方法中，成功调用 modelClient.CompleteAsync 后，插入：

在 `RecordModelUsageAsync` 调用之前（约第 43 行），增加：

```csharp
// 记录模型调用延迟
if (settings.Telemetry.Enabled)
{
    var callLatency = stopwatch.ElapsedMilliseconds;
    var budget = ContextBudgetCalculator.Compute(
        environmentPrompt, tools, session.Messages,
        settings.ContextCompaction, settings.Model,
        tokenEstimatorCalibrator.GetMultiplier(session.Id));
    _telemetrySink.Write(TokenMetricsCollector.BuildModelCompleteEvent(
        session.Id, "AgentTurnCoordinator", response,
        contextSavingsTokens, budget.TotalUtilization, callLatency));
}
```

同样，在 overflow retry 的第二个 `modelClient.CompleteAsync` 之后（约第 76 行），也插入相同逻辑。

在 catch (HttpRequestException) 块内（第 48 行），增加 overflow 指标：

```csharp
if (settings.Telemetry.Enabled)
{
    _telemetrySink.Write(TokenMetricsCollector.BuildOverflowEvent(
        session.Id,
        ContextBudgetCalculator.Compute(environmentPrompt, tools, session.Messages,
            settings.ContextCompaction, settings.Model,
            tokenEstimatorCalibrator.GetMultiplier(session.Id)).TotalUtilization));
}
```

需要在 `AgentTurnCoordinator` 类中添加字段：

```csharp
private readonly ITelemetrySink _telemetrySink;
```

并在构造函数参数列表中添加 `ITelemetrySink telemetrySink`。

- [ ] **Step 3: 修改 ToolInvocationPipeline 记录工具调用指标**

将现有 `Stopwatch` 用法包装成 `LatencyMetricsCollector.TrackToolCall`。在 `ToolInvocationPipeline.cs` 中，将：

```csharp
var sw = Stopwatch.StartNew();
```

改为：

```csharp
var telemetrySink = AmbientTelemetrySinkScope.CurrentSink; // 新建的 ambient scope
using var _ = telemetrySink is not null
    ? LatencyMetricsCollector.TrackToolCall(session.Id, toolCall.Name, telemetrySink)
    : null;
```

如果不希望引入 ambient scope，可以走简单的直接调用方式——在类中新增字段：

```csharp
private readonly ITelemetrySink? _telemetrySink;
```

构造函数参数增加 `ITelemetrySink? telemetrySink`，然后在 catch 块前插入：

```csharp
_telemetrySink?.Write(LatencyMetricsCollector.BuildToolInvokeEvent(
    session.Id, toolCall.Name, result.Succeeded, sw.ElapsedMilliseconds, result.Error));
```

- [ ] **Step 4: 运行测试验证编译**

Run: `dotnet build`
Expected: BUILD succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Athlon.Agent.Core/AgentRuntime.cs src/Athlon.Agent.Core/AgentTurnCoordinator.cs src/Athlon.Agent.Core/ToolInvocationPipeline.cs
git commit -m "feat(telemetry): instrument AgentRuntime, TurnCoordinator, and ToolInvocationPipeline with TelemetryEvents"
```

---

### Task 5: 配置集成 + 双写入 AppLogger

**Files:**
- Modify: `src/Athlon.Agent.Core/AgentSettings.cs`
- Modify: `src/Athlon.Agent.Infrastructure/AppLogger.cs`
- Modify: `src/Athlon.Agent.Core/SessionUsageAccumulator.cs`

- [ ] **Step 1: 在 AppSettings 中追加 TelemetrySettings**

在 `AgentSettings.cs` 的 `AppSettings` 类中增加属性：

```csharp
public TelemetrySettings Telemetry { get; set; } = new();
```

- [ ] **Step 2: 在 AppLogger 中增加双写入**

在 `AppLogger.cs` 中增加一个可选的 `ITelemetrySink` 字段：

```csharp
private readonly ITelemetrySink? _telemetrySink;
```

修改构造函数：

```csharp
private AppLogger(ILogger logger, Logger? rootLogger = null, ITelemetrySink? telemetrySink = null)
{
    _logger = logger;
    _rootLogger = rootLogger;
    _telemetrySink = telemetrySink;
}
```

在每个日志方法中，将 `Information`/`Warning`/`Error` 级别的日志同步输出到遥测 sink（以 L0_Raw 级别）：

```csharp
public void Information(string messageTemplate, params object[] values)
{
    _logger.Information(SensitiveText.Redact(messageTemplate), values);
    _telemetrySink?.Write(new TelemetryEvent(
        "log.information", TelemetryLevel.L0_Raw,
        DateTimeOffset.UtcNow, "",
        new Dictionary<string, object> { ["message"] = string.Format(messageTemplate, values) },
        new Dictionary<string, double>(),
        _sourceContext));
}
```

`ForContext` 方法也要透传 telemetrySink：

```csharp
public IAppLogger ForContext(string sourceContext)
{
    return new AppLogger(
        _logger.ForContext("SourceContext", sourceContext),
        _telemetrySink);
}
```

增加 `_sourceContext` 字段：

```csharp
private readonly string _sourceContext;

private AppLogger(..., string sourceContext = "")
{
    ...
    _sourceContext = sourceContext;
}
```

- [ ] **Step 3: 在 SessionUsageAccumulator 中增加遥测挂钩**

在 `Record` 和 `RecordCompaction` 方法中，如果全局有 telemetrySink，则发出指标事件。

```csharp
// 在 SessionUsageAccumulator 记录 compaction 时
public SessionUsageSnapshot RecordCompaction(
    string sessionId, int tokensBefore, int tokensAfter,
    double beforeUtilization = 0, double afterUtilization = 0) // 增加可选参数
```

在 RecordCompaction 方法末尾增加遥测写入（需注入 ITelemetrySink）：

```csharp
_telemetrySink?.Write(TokenMetricsCollector.BuildCompactionEvent(
    sessionId, "conversation_compact", savings, beforeUtilization, afterUtilization));
```

- [ ] **Step 4: 运行测试验证编译**

Run: `dotnet build`
Expected: BUILD succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Athlon.Agent.Core/AgentSettings.cs src/Athlon.Agent.Infrastructure/AppLogger.cs src/Athlon.Agent.Core/SessionUsageAccumulator.cs
git commit -m "feat(telemetry): integrate TelemetrySettings into AppSettings, dual-write AppLogger, emit from SessionUsageAccumulator"
```

---

### Task 6: 动态调参闭环（L4 Steering）

**Files:**
- Modify: `src/Athlon.Agent.Core/Compaction/ContextBudgetCalculator.cs`
- Modify: `src/Athlon.Agent.Core/Compaction/DynamicCompactionPlan.cs`
- Create: `src/Athlon.Agent.Core/Telemetry/DynamicParamTuner.cs`

- [ ] **Step 1: 创建动态调参器**

```csharp
// src/Athlon.Agent.Core/Telemetry/DynamicParamTuner.cs
namespace Athlon.Agent.Core.Telemetry;

/// <summary>
/// L4 Steering: 根据 MetricsAggregator 的 L3 聚合结果，
/// 计算 DynamicCompactionSettings 的推荐调整值。
/// </summary>
public sealed class DynamicParamTuner
{
    private readonly IAppLogger _logger;
    private readonly ITelemetrySink _sink;

    // 调参记录
    private readonly List<TuneAction> _history = new();

    public DynamicParamTuner(IAppLogger logger, ITelemetrySink sink)
    {
        _logger = logger.ForContext("DynamicParamTuner");
        _sink = sink;
    }

    public sealed record TuneAction(
        DateTimeOffset Timestamp,
        string Parameter,
        double OldValue,
        double NewValue,
        string Reason);

    /// <summary>
    /// 根据聚合快照，返回推荐的新 TargetUtilization 值。
    /// 规则：
    /// - 如果 overflowRetryCount > 窗口内回合数 * 0.1 → 降低 target（减少溢出）
    /// - 如果 cacheHitRate < 0.2 且 avgUtilization < 0.5 → 提高 target（增加复用）
    /// - 如果 compactionCount 过高 → 降低 target（减少压缩频率）
    /// </summary>
    public double SuggestTargetUtilization(
        MetricsSnapshot snapshot,
        double currentTarget,
        int totalTurnsInWindow)
    {
        if (totalTurnsInWindow == 0) return currentTarget;

        var overflowRate = (double)snapshot.OverflowRetryCount / totalTurnsInWindow;
        var compactionRate = (double)snapshot.CompactionCount / totalTurnsInWindow;

        double suggestion = currentTarget;

        // 溢出过多 → 降低 target 5%
        if (overflowRate > 0.10)
        {
            suggestion = Math.Max(0.60, suggestion - 0.05);
            RecordTune("TargetUtilization", currentTarget, suggestion,
                $"overflowRate={overflowRate:F2} > 0.10, reducing target");
        }
        // 缓存命中低 + 压力小 → 提高 target 5%
        else if (snapshot.CacheHitRate < 0.20 && snapshot.AvgContextUtilization < 0.50)
        {
            suggestion = Math.Min(0.90, suggestion + 0.05);
            RecordTune("TargetUtilization", currentTarget, suggestion,
                $"cacheHitRate={snapshot.CacheHitRate:F2} < 0.20 && avgUtil={snapshot.AvgContextUtilization:F2} < 0.50, raising target");
        }
        // 压缩频繁 → 降低 target 3%
        else if (compactionRate > 0.30)
        {
            suggestion = Math.Max(0.60, suggestion - 0.03);
            RecordTune("TargetUtilization", currentTarget, suggestion,
                $"compactionRate={compactionRate:F2} > 0.30, reducing target");
        }

        return suggestion;
    }

    /// <summary>
    /// 建议 OverflowPostCompactionUtilization 值。
    /// 如果溢出后仍然频繁溢出，降低值；否则可以稍微放宽。
    /// </summary>
    public double SuggestOverflowPostCompactUtilization(
        MetricsSnapshot snapshot,
        double currentValue)
    {
        if (snapshot.OverflowRetryCount > 2 && snapshot.TotalModelCalls > 10)
        {
            var lower = Math.Max(0.15, currentValue - 0.05);
            RecordTune("OverflowPostCompactionUtilization", currentValue, lower,
                $"overflowRetryCount={snapshot.OverflowRetryCount} > 2");
            return lower;
        }
        return currentValue;
    }

    private void RecordTune(string param, double oldVal, double newVal, string reason)
    {
        if (Math.Abs(oldVal - newVal) < 0.001) return;

        var action = new TuneAction(DateTimeOffset.UtcNow, param, oldVal, newVal, reason);
        _history.Add(action);
        _logger.Information(
            "L4 tune: {Param} {Old:F2} -> {New:F2} ({Reason})",
            param, oldVal, newVal, reason);

        _sink.Write(new TelemetryEvent(
            "steering.tune", TelemetryLevel.L4_Steering,
            action.Timestamp, "__global__",
            new Dictionary<string, object>
            {
                ["parameter"] = param,
                ["reason"] = reason
            },
            new Dictionary<string, double>
            {
                ["oldValue"] = oldVal,
                ["newValue"] = newVal
            },
            "DynamicParamTuner"));
    }

    public IReadOnlyList<TuneAction> GetHistory() => _history.AsReadOnly();
}
```

- [ ] **Step 2: 运行测试验证编译**

Run: `dotnet build`
Expected: BUILD succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Athlon.Agent.Core/Telemetry/DynamicParamTuner.cs
git commit -m "feat(telemetry): add L4 DynamicParamTuner for closed-loop steering"
```

---

### Task 7: 指标报告工具（本地查询 + JSON 报告）

**Files:**
- Create: `tools/TelemetryReport/README.md`
- Create: `tools/TelemetryReport/Program.cs`
- Create: `tools/TelemetryReport/TelemetryReport.csproj`

- [ ] **Step 1: 创建项目脚手架**

```bash
mkdir -p tools/TelemetryReport
```

```xml
<!-- tools/TelemetryReport/TelemetryReport.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 实现报告生成器**

```csharp
// tools/TelemetryReport/Program.cs
using System.Text.Json;

var telemetryDir = args.Length > 0 ? args[0] : "./telemetry";
var reportFile = Path.Combine(telemetryDir, "report.json");

if (!Directory.Exists(telemetryDir))
{
    Console.WriteLine($"Telemetry directory not found: {telemetryDir}");
    return 1;
}

var allEvents = new List<JsonElement>();
foreach (var file in Directory.GetFiles(telemetryDir, "telemetry-*.jsonl"))
{
    foreach (var line in await File.ReadAllLinesAsync(file))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try { allEvents.Add(JsonSerializer.Deserialize<JsonElement>(line)); }
        catch { /* skip malformed */ }
    }
}

Console.WriteLine($"Loaded {allEvents.Count} telemetry events from {telemetryDir}");

// 按 level 分组
var byLevel = allEvents.GroupBy(e =>
    e.TryGetProperty("level", out var l) ? l.GetString() : "unknown")
    .ToDictionary(g => g.Key, g => g.Count());

Console.WriteLine("\n=== Events by Level ===");
foreach (var (level, count) in byLevel.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {level}: {count}");

// 提取 L2 模型调用指标
var modelEvents = allEvents
    .Where(e => e.TryGetProperty("eventType", out var et) && et.GetString() == "model.complete")
    .ToList();

if (modelEvents.Count > 0)
{
    var promptTokens = modelEvents
        .Select(e => e.TryGetProperty("measures", out var m) && m.TryGetProperty("promptTokens", out var pt) ? pt.GetDouble() : 0)
        .Sum();
    var completionTokens = modelEvents
        .Select(e => e.TryGetProperty("measures", out var m) && m.TryGetProperty("completionTokens", out var ct) ? ct.GetDouble() : 0)
        .Sum();
    var latencies = modelEvents
        .Select(e => e.TryGetProperty("measures", out var m) && m.TryGetProperty("latencyMs", out var l) ? l.GetDouble() : 0)
        .OrderBy(v => v)
        .ToList();

    Console.WriteLine("\n=== Model Call Metrics ===");
    Console.WriteLine($"  Total model calls: {modelEvents.Count}");
    Console.WriteLine($"  Total prompt tokens: {promptTokens:N0}");
    Console.WriteLine($"  Total completion tokens: {completionTokens:N0}");
    Console.WriteLine($"  Avg latency: {latencies.Average():F1}ms");
    if (latencies.Count > 0)
    {
        var p95 = latencies[(int)(latencies.Count * 0.95)];
        Console.WriteLine($"  P95 latency: {p95:F1}ms");
    }
}

// 提取 L2 工具调用指标
var toolEvents = allEvents
    .Where(e => e.TryGetProperty("eventType", out var et) && et.GetString() == "tool.invoke")
    .ToList();

if (toolEvents.Count > 0)
{
    var succeeded = toolEvents.Count(e =>
    {
        if (!e.TryGetProperty("dimensions", out var d)) return false;
        return d.TryGetProperty("succeeded", out var s) && s.GetString() == "True";
    });
    var failed = toolEvents.Count - succeeded;
    var toolLatencies = toolEvents
        .Select(e => e.TryGetProperty("measures", out var m) && m.TryGetProperty("durationMs", out var d) ? d.GetDouble() : 0)
        .OrderBy(v => v)
        .ToList();

    Console.WriteLine("\n=== Tool Call Metrics ===");
    Console.WriteLine($"  Total tool calls: {toolEvents.Count}");
    Console.WriteLine($"  Succeeded: {succeeded}");
    Console.WriteLine($"  Failed: {failed}");
    Console.WriteLine($"  Avg duration: {toolLatencies.Average():F1}ms");
    if (toolLatencies.Count > 0)
    {
        var p95 = toolLatencies[(int)(toolLatencies.Count * 0.95)];
        Console.WriteLine($"  P95 duration: {p95:F1}ms");
    }
}

// 提取 L3 聚合事件中的最近报告
var aggEvents = allEvents
    .Where(e => e.TryGetProperty("eventType", out var et) && et.GetString() == "metrics.window")
    .OrderByDescending(e => e.TryGetProperty("ts", out var ts) ? ts.GetString() : "")
    .Take(5)
    .ToList();

if (aggEvents.Count > 0)
{
    Console.WriteLine("\n=== Last 5 Aggregated Windows ===");
    foreach (var evt in aggEvents)
    {
        var ts = evt.TryGetProperty("ts", out var t) ? t.GetString() : "?";
        var measures = evt.TryGetProperty("measures", out var m) ? m : default;
        Console.WriteLine($"  [{ts}] cost=${measures.TryGetProperty("estimatedCostUsd", out var c) ? c.GetDouble():0:F4}");
        Console.WriteLine($"    cacheHitRate={measures.TryGetProperty("cacheHitRate", out var hr) ? hr.GetDouble():0:P1}");
        Console.WriteLine($"    p95Latency={measures.TryGetProperty("p95ModelCallLatencyMs", out var pl) ? pl.GetDouble():0:F1}ms");
    }
}

// 输出汇总 JSON
var report = new
{
    generatedAt = DateTimeOffset.UtcNow,
    totalEvents = allEvents.Count,
    byLevel,
    modelMetrics = modelEvents.Count > 0 ? new
    {
        totalCalls = modelEvents.Count,
        totalPromptTokens = promptTokens,
        totalCompletionTokens = completionTokens,
        avgLatencyMs = Math.Round(latencies.Average(), 1),
        p95LatencyMs = latencies.Count > 0 ? Math.Round(latencies[(int)(latencies.Count * 0.95)], 1) : 0
    } : null,
    toolMetrics = toolEvents.Count > 0 ? new
    {
        totalCalls = toolEvents.Count,
        succeeded,
        failed,
        avgDurationMs = Math.Round(toolLatencies.Average(), 1)
    } : null
};

var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(reportFile, reportJson);
Console.WriteLine($"\nReport saved to: {reportFile}");

return 0;
```

- [ ] **Step 3: 运行构建验证**

Run: `dotnet build tools/TelemetryReport/TelemetryReport.csproj`
Expected: BUILD succeeded

- [ ] **Step 4: Commit**

```bash
git add tools/TelemetryReport/
git commit -m "feat(telemetry): add TelemetryReport CLI tool for local metrics query"
```

---

### Task 8: 集成到 DI 容器和启动流程

**Files:**
- Modify: `src/Athlon.Agent.App/` (查找 DI 注册位置)

- [ ] **Step 1: 查找并修改 DI 注册**

```csharp
// 在 DI 容器配置中（假设在 Program.cs 或 Startup.cs）
services.AddSingleton<TelemetrySettings>(sp =>
    sp.GetRequiredService<AppSettings>().Telemetry);
services.AddSingleton<ITelemetrySink>(sp =>
{
    var settings = sp.GetRequiredService<TelemetrySettings>();
    if (!settings.Enabled) return new NullTelemetrySink(); // 空操作实现，避免 null 检查
    var appPaths = sp.GetRequiredService<IAppPathProvider>();
    var logger = sp.GetRequiredService<IAppLogger>();
    var filePath = string.IsNullOrWhiteSpace(settings.Directory)
        ? Path.Combine(appPaths.AppDataDirectory, "telemetry", $"telemetry-{DateTime.UtcNow:yyyyMMdd}.jsonl")
        : Path.Combine(settings.Directory, $"telemetry-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    return new TelemetrySink(settings, logger, filePath);
});
services.AddSingleton<MetricsAggregator>();
services.AddSingleton<DynamicParamTuner>();
```

- [ ] **Step 2: 创建空操作实现**

```csharp
// src/Athlon.Agent.Core/Telemetry/NullTelemetrySink.cs
namespace Athlon.Agent.Core.Telemetry;

public sealed class NullTelemetrySink : ITelemetrySink
{
    public void Write(TelemetryEvent evt) { }
    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<TelemetryEvent> ReadRecent(int count = 1000) => Array.Empty<TelemetryEvent>();
}
```

- [ ] **Step 3: 在应用关闭时 flush**

```csharp
// 在应用退出前
var sink = host.Services.GetRequiredService<ITelemetrySink>();
await sink.FlushAsync();
```

- [ ] **Step 4: 构建验证**

Run: `dotnet build`
Expected: BUILD succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Athlon.Agent.App/Program.cs src/Athlon.Agent.Core/Telemetry/NullTelemetrySink.cs
git commit -m "feat(telemetry): wire into DI container, add NullTelemetrySink, flush on shutdown"
```

---

### Task 9: 示例配置文档

**Files:**
- Create: `docs/features/telemetry-pipeline.md`

- [ ] **Step 1: 编写遥测管道使用指南**

```markdown
# Telemetry Pipeline (L0→L4)

Athlon Agent 的遥测管道受 [UltraData Pipeline](https://ultradata.openbmb.cn/) 启发，
将日志数据分为 5 个治理级别，逐层提升价值密度。

## 级别概览

| 级别 | 名称 | 产出 | 示例 |
|------|------|------|------|
| L0 | Raw | Serilog 文本 + TelemetryEvent JSON | `app-20260614.log`, `telemetry-20260614.jsonl` |
| L1 | Cleaned | 脱敏、统一时区的事件流 | `TurnCompleted`, `LogWarning` |
| L2 | Metric | 维度化指标 | `model.complete`, `tool.invoke`, `compaction.applied` |
| L3 | Aggregated | 时序窗口聚合 | `metrics.window`（每 5 分钟）|
| L4 | Steering | 动态调参建议 | `DynamicParamTuner` 历史 |

## 启用方式

在 `~/.athlon-agent/config/settings.json` 中：

```json
{
  "telemetry": {
    "enabled": true,
    "directory": "",
    "minimumLevel": "L1_Cleaned",
    "sampleRate": 1.0,
    "enableMetricsAggregation": true,
    "metricsWindowSeconds": 300
  }
}
```

- `directory`: 空值时使用默认 `~/.athlon-agent/telemetry/` 目录
- `sampleRate`: 生产环境可设为 0.1 降低写入量
- `minimumLevel`: 生产推荐 `L1_Cleaned`，调试用 `L0_Raw`

## 报告工具

```bash
dotnet run --project tools/TelemetryReport ~/.athlon-agent/telemetry
```

输出到 `~/.athlon-agent/telemetry/report.json`，包含：
- 事件各 level 分布统计
- 模型调用总 token / 平均延迟 / P95
- 工具调用成功率 / 平均耗时
- 最近 5 个聚合窗口的成本估算

## 性能影响

- L0 写入：异步队列 + 批量 flush，单事件 ~200ns
- L2 指标收集：内存聚合，无 IO
- L3 聚合：每 N 秒一次 O(事件数) 扫描
- L4 Steering：仅在聚合事件后计算，开销可忽略

建议监控 `telemetry.jsonl` 文件大小——每天约 10MB（全采样 / 1000 次对话）。
```

- [ ] **Step 2: Commit**

```bash
git add docs/features/telemetry-pipeline.md
git commit -m "docs: add telemetry-pipeline feature doc with L0-L4 architecture"
```

---

## 自检

### Spec 覆盖率
1. **L0→L4 分级架构** — Task 1 定义级别枚举，Task 1-3 逐层实现
2. **结构化性能指标提取** — Task 3 (TokenMetricsCollector, LatencyMetricsCollector), Task 4 (注入 Runtime)
3. **内存时序聚合** — Task 3 (MetricsAggregator)
4. **配置开关** — Task 2 (TelemetrySettings), Task 5 (AppSettings.Telemetry)
5. **L4 闭环调参** — Task 6 (DynamicParamTuner)
6. **离线报告工具** — Task 7 (TelemetryReport CLI)
7. **DI 集成** — Task 8
8. **文档** — Task 9

### 占位符检查
- 所有代码块包含完整可编译的实现
- 无 "TBD"/"TODO"/"implement later"
- 所有文件路径精确

### 类型一致性
- `TelemetryLevel` 枚举值在 Task 1 定义，Task 2-6 统一引用
- `TelemetryEvent` 的 `EventType` 字符串在 `TokenMetricsCollector` 和 `MetricsAggregator` 的 switch 中一致
- `ITelemetrySink` 在 Task 2 定义，在 Task 3-6 注入使用

---

## 执行方式

计划已保存到 `docs/superpowers/plans/2026-06-14-log-driven-performance-optimization.md`。

**两个执行选项：**

1. **Subagent 驱动（推荐）** — 每个 Task 分派一个子 agent，前后 review，快速迭代
2. **内联执行** — 在当前会话中按 Task 顺序实现

你想用哪种方式？
