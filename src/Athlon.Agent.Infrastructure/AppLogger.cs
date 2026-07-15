using Athlon.Agent.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class AppLogger : IAppLogger, IDisposable
{
    internal const string ChinaTimestampPropertyName = "ChinaTimestamp";

    private const string FileOutputTemplate =
        "{" + ChinaTimestampPropertyName + ":yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private readonly ILogger _logger;
    private readonly Logger? _rootLogger;

    private AppLogger(ILogger logger, Logger? rootLogger = null)
    {
        _logger = logger;
        _rootLogger = rootLogger;
    }

    public static AppLogger Create(LoggingSettings settings, string defaultLogDirectory)
    {
        var logDirectory = string.IsNullOrWhiteSpace(settings.Directory)
            ? defaultLogDirectory
            : settings.Directory;
        Directory.CreateDirectory(logDirectory);

        var level = Enum.TryParse<LogEventLevel>(settings.MinimumLevel, true, out var parsed) ? parsed : LogEventLevel.Information;
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.With<ChinaTimestampEnricher>()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.RetainedDays,
                fileSizeLimitBytes: settings.MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: FileOutputTemplate)
            .CreateLogger();

        return new AppLogger(logger, logger);
    }

    public void Debug(string messageTemplate, params object[] values) => _logger.Debug(SensitiveText.Redact(messageTemplate), values);
    public void Information(string messageTemplate, params object[] values) => _logger.Information(SensitiveText.Redact(messageTemplate), values);
    public void Warning(string messageTemplate, params object[] values) => _logger.Warning(SensitiveText.Redact(messageTemplate), values);
    public void Error(Exception exception, string messageTemplate, params object[] values) => _logger.Error(exception, SensitiveText.Redact(messageTemplate), values);
    public IAppLogger ForContext(string sourceContext) => new AppLogger(_logger.ForContext("SourceContext", sourceContext));
    public void Dispose() => _rootLogger?.Dispose();
}

/// <summary>Forces Serilog file timestamps into China Standard Time (UTC+8).</summary>
internal sealed class ChinaTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var china = AppTimeZone.ToChina(logEvent.Timestamp);
        logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(AppLogger.ChinaTimestampPropertyName, china));
    }
}
