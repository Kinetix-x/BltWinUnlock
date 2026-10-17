using System.Text;

namespace Btwu.Core;

/// <summary>Rolling file log plus optional console echo. Logging must never take the agent down.</summary>
public static class Log
{
    const long MaxBytes = 2 * 1024 * 1024;
    static readonly object Gate = new();

    // Encoding.UTF8 emits a BOM on the first write, which looks like garbage at the top of a log.
    static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool Echo { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            // The echo is guarded for the same reason the file write below is: a console that has been
            // detached (see ConsoleWindow.DetachForResidentMode) or redirected to a broken pipe makes
            // Console.WriteLine throw, and logging must never be able to take the caller down — least
            // of all the heartbeat loop, whose whole job is to keep publishing state.
            if (Echo)
            {
                try { Console.WriteLine(line); } catch { }
            }

            try
            {
                Paths.EnsureDataDir();
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(Paths.LogFile, Paths.LogFile + ".1", overwrite: true);
                }
                File.AppendAllText(Paths.LogFile, line + Environment.NewLine, Utf8NoBom);
            }
            catch
            {
                // never let logging break the caller
            }
        }
    }
}
