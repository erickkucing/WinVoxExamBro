using ExamBro.Core.Models;
using Serilog;
using Serilog.Events;

namespace ExamBro.Core.Logging;

/// <summary>
/// Factory for Serilog <see cref="ILogger"/> instances.
/// </summary>
public static class LoggerSetup
{
    /// <summary>
    /// Build a logger from the given config (or defaults when config is unavailable).
    /// Writes rolling daily files to the configured log path and emits to the console
    /// when attached.
    /// </summary>
    public static ILogger CreateLogger(ExamConfig? config = null)
    {
        string logPath = config?.Logging.LogPath ?? Constants.LogFolder;
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Constants.LogFolder;

        // Expand environment variables like %ProgramData%
        logPath = Environment.ExpandEnvironmentVariables(logPath);

        if (!Directory.Exists(logPath))
            Directory.CreateDirectory(logPath);

        string filePath = Path.Combine(logPath, "exambro-.log");

        LogEventLevel minLevel = LogEventLevel.Information;
        if (config != null && Enum.TryParse<LogEventLevel>(config.Logging.LogLevel, true, out var parsed))
            minLevel = parsed;

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("UserName", Environment.UserName)
            .WriteTo.File(
                filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 90,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{MachineName}/{UserName}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

        // Optionally add webhook sink
        if (!string.IsNullOrWhiteSpace(config?.Logging.WebhookUrl))
        {
            loggerConfig.WriteTo.Sink(
                new WebhookSink(config.Logging.WebhookUrl, config.Logging.WebhookHmacKey));
        }

        return loggerConfig.CreateLogger();
    }
}
