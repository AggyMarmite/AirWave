using System.Collections.Concurrent;

namespace AirWave.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Tiny thread-safe logger. The app shows recent lines on its diagnostics page and can save them to a file.</summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> Recent = new();
    private const int MaxRecent = 2000;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Raised for every line written (level, formatted line).</summary>
    public static event Action<LogLevel, string> Written;

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);

    public static void Write(LogLevel level, string msg)
    {
        if (level < MinLevel) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[0]}] {msg}";
        Recent.Enqueue(line);
        while (Recent.Count > MaxRecent && Recent.TryDequeue(out _)) { }
        try { Written?.Invoke(level, line); } catch { }
    }

    public static string[] Snapshot() => Recent.ToArray();
}
