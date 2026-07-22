using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Recorder.Common.Logging;

/// <summary>
/// Central Serilog configuration: rolling daily files plus console output for the
/// dev tools. Warning+ by default; verbose is opt-in because per-frame logging at
/// 120 FPS would itself become a performance problem.
/// </summary>
public static class RecorderLog
{
    /// <summary>
    /// Builds the process-wide logger. Called once at startup by every executable
    /// (app, PoC tools, benchmarks) so all components share one log format and location.
    /// </summary>
    public static Logger CreateLogger(string logDirectory, bool verbose)
    {
        Directory.CreateDirectory(logDirectory);

        LogEventLevel minimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logDirectory, "recorder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: minimumLevel)
            .CreateLogger();
    }
}
