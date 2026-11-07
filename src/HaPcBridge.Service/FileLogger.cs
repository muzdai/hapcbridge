using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HaPcBridge.Service;

/// <summary>Daily log files in %ProgramData%\HaPcBridge\logs, last 7 days kept.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    readonly string _dir;
    readonly BlockingCollection<string> _queue = new(4096);
    readonly Thread _thread;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Cleanup();
        _thread = new Thread(Pump) { IsBackground = true, Name = "file-log" };
        _thread.Start();
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category.Split('.').Last());

    internal void Enqueue(string line) => _queue.TryAdd(line);

    void Pump()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path.Combine(_dir, $"hapcbridge-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch { /* disk full / locked: drop */ }
        }
    }

    void Cleanup()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_dir).GetFiles("hapcbridge-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
                f.Delete();
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1000);
    }

    sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var lvl = level switch { LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "INF" };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} [{category}] {fmt(state, ex)}";
            if (ex != null) line += Environment.NewLine + ex;
            p.Enqueue(line);
        }
    }
}
