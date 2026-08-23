using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DocVault.Desktop.Auth;

/// <summary>
/// Logging for a GUI app, which has three problems a console app does not:
/// there is no console attached, the interesting failures happen in a browser
/// round-trip you cannot step through, and the user is usually not running under
/// a debugger when it breaks.
/// </summary>
/// <remarks>
/// So everything goes to three places at once:
/// <list type="bullet">
///   <item>a file under <c>%LOCALAPPDATA%\DocVault\logs</c> — the one that survives</item>
///   <item><see cref="Debug"/> — the Visual Studio Output window</item>
///   <item><see cref="Console"/> — visible when launched from a terminal</item>
/// </list>
/// </remarks>
public static class DesktopLogging
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DocVault", "logs");

    /// <summary>The file this process is writing to. Surface it in the UI.</summary>
    public static string? CurrentLogFile { get; private set; }

    public static ILoggerFactory Create(LogLevel minimum = LogLevel.Debug, string? directory = null)
    {
        var dir = directory ?? LogDirectory;
        Directory.CreateDirectory(dir);

        // One file per run, so a failed sign-in is not interleaved with earlier ones.
        CurrentLogFile = Path.Combine(dir, $"docvault-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        PruneOldLogs(dir);

        // Create it now rather than on first write, for two reasons: the UI can show
        // a path that exists, and an unwritable directory fails here at startup
        // instead of silently swallowing the log entries you most needed.
        File.AppendAllText(
            CurrentLogFile,
            $"DocVault desktop — {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimum);
            builder.AddProvider(new MultiSinkLoggerProvider(CurrentLogFile));
        });
    }

    /// <summary>Keeps the 20 most recent logs; these are diagnostics, not an audit trail.</summary>
    private static void PruneOldLogs(string directory)
    {
        try
        {
            foreach (var old in new DirectoryInfo(directory)
                         .GetFiles("docvault-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(20))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
            // Housekeeping must never stop the app starting.
        }
    }
}

internal sealed class MultiSinkLoggerProvider(string? filePath) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new MultiSinkLogger(this, categoryName);

    internal void Write(string line)
    {
        // Debug goes to the Visual Studio Output window; Console shows up when the
        // app was launched from a terminal. Neither is guaranteed, which is why the
        // file exists.
        Debug.WriteLine(line);
        Console.WriteLine(line);

        if (filePath is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // A locked or full disk must not take the app down mid-sign-in.
        }
    }

    public void Dispose() { }
}

internal sealed class MultiSinkLogger(MultiSinkLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var shortCategory = category.Split('.').LastOrDefault() ?? category;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {Abbreviate(logLevel)} {shortCategory,-24} {formatter(state, exception)}";

        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        provider.Write(line);
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
