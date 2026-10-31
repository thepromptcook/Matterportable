using System;
using System.Collections.Generic;

namespace Matterportable.Core
{
    public enum LogLevel { Info, Warning, Error }

    /// <summary>Engine-agnostic logging sink. The Unity layer forwards to Debug.Log; the CLI writes to stdout.</summary>
    public interface ILogSink
    {
        void Log(LogLevel level, string message);
    }

    public static class LogExtensions
    {
        public static void Info(this ILogSink log, string message) => log?.Log(LogLevel.Info, message);
        public static void Warn(this ILogSink log, string message) => log?.Log(LogLevel.Warning, message);
        public static void Error(this ILogSink log, string message) => log?.Log(LogLevel.Error, message);
    }

    public sealed class NullLog : ILogSink
    {
        public static readonly NullLog Instance = new NullLog();
        public void Log(LogLevel level, string message) { }
    }

    public sealed class ConsoleLog : ILogSink
    {
        public bool Verbose = true;

        public void Log(LogLevel level, string message)
        {
            if (level == LogLevel.Info && !Verbose) return;
            var prefix = level == LogLevel.Error ? "error: " : level == LogLevel.Warning ? "warning: " : "";
            var w = level == LogLevel.Info ? Console.Out : Console.Error;
            w.WriteLine(prefix + message);
        }
    }

    /// <summary>Thread-safe log that keeps the lines (shown in the import window, written to disk for bug reports).</summary>
    public sealed class BufferLog : ILogSink
    {
        readonly object _lock = new object();
        readonly List<KeyValuePair<LogLevel, string>> _lines = new List<KeyValuePair<LogLevel, string>>();
        public ILogSink Forward;

        public void Log(LogLevel level, string message)
        {
            lock (_lock) _lines.Add(new KeyValuePair<LogLevel, string>(level, message));
            Forward?.Log(level, message);
        }

        public List<KeyValuePair<LogLevel, string>> Snapshot()
        {
            lock (_lock) return new List<KeyValuePair<LogLevel, string>>(_lines);
        }

        public string Text()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var l in Snapshot())
                sb.Append(l.Key == LogLevel.Info ? "" : l.Key + ": ").Append(l.Value).Append('\n');
            return sb.ToString();
        }
    }
}
