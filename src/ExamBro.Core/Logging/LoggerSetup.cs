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
        LogEventLevel minLevel = LogEventLevel.Information;
        if (config != null && Enum.TryParse<LogEventLevel>(config.Logging.LogLevel, true, out var parsed))
            minLevel = parsed;

        string targetFolder = ResolveWritableLogDirectory(config?.Logging.LogPath);

        try
        {
            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(minLevel)
                .Enrich.WithProperty("MachineName", Environment.MachineName)
                .Enrich.WithProperty("UserName", Environment.UserName);

            if (!string.IsNullOrWhiteSpace(targetFolder))
            {
                string filePath = Path.Combine(targetFolder, "exambro-.log");
                loggerConfig.WriteTo.File(
                    filePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 90,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{MachineName}/{UserName}] {Message:lj}{NewLine}{Exception}");
            }

            loggerConfig.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

            if (!string.IsNullOrWhiteSpace(config?.Logging.WebhookUrl))
            {
                loggerConfig.WriteTo.Sink(
                    new WebhookSink(config.Logging.WebhookUrl, config.Logging.WebhookHmacKey));
            }

            return loggerConfig.CreateLogger();
        }
        catch
        {
            // Fallback to console-only logger so application startup never fails on logging errors
            try
            {
                return new LoggerConfiguration()
                    .MinimumLevel.Is(minLevel)
                    .WriteTo.Console()
                    .CreateLogger();
            }
            catch
            {
                return Serilog.Core.Logger.None;
            }
        }
    }

    private static string ResolveWritableLogDirectory(string? configuredPath)
    {
        var candidatePaths = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidatePaths.Add(Environment.ExpandEnvironmentVariables(configuredPath));

        candidatePaths.Add(Constants.LogFolder);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidatePaths.Add(Path.Combine(localAppData, Constants.AppName, "logs"));

        candidatePaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"));
        candidatePaths.Add(Path.Combine(Path.GetTempPath(), "LouisVoxExamBro", "logs"));

        foreach (string candidate in candidatePaths)
        {
            try
            {
                if (!Directory.Exists(candidate))
                    Directory.CreateDirectory(candidate);

                string testFile = Path.Combine(candidate, $".probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "probe");
                File.Delete(testFile);

                return candidate;
            }
            catch
            {
                // Try next directory candidate
            }
        }

        return string.Empty;
    }
}
