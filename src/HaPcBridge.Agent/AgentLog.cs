namespace HaPcBridge.Agent;

/// <summary>Small diagnostic log of the agent: %LocalAppData%\HaPcBridge\agent.log.</summary>
internal static class AgentLog
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaPcBridge", "agent.log");

    static readonly object Gate = new();
    static string? _last;

    /// <summary>Writes a line; the same text repeated in a row (a retry loop) is written once.</summary>
    public static void Write(string text)
    {
        lock (Gate)
        {
            if (text == _last) return;
            _last = text;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 256 * 1024) file.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch { /* diagnostics only */ }
        }
    }
}
