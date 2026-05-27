using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class AppLogger : IAppLogger, IDisposable
{
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
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: settings.RetainedDays,
                fileSizeLimitBytes: settings.MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                shared: true)
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
