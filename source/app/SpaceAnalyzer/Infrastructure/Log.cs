using System.Globalization;
using System.Text;

namespace SpaceAnalyzer.App.Infrastructure;

/// <summary>
/// Application log. Two destinations:
/// - opcua-client.log in the data folder (5 MiB × 5 backups, same line format as the 2.x logging module), shown by the interface;
/// - the "Log" folder beside the executable (<see cref="ConfigureFolder"/>), one file per day and kind, kept for 30 days:
///   SFA_funzionamento_AAAA-MM-GG.log (every entry), SFA_errori_AAAA-MM-GG.log (warnings and errors only) and
///   SFA_stato_AAAA-MM-GG.log (periodic snapshot written by <see cref="StatusLog"/>). A long acquisition keeps its whole history
///   there, while the rotating log can lose the hours before a problem.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static string? path, folder;
    const long MaxBytes = 5 * 1024 * 1024;
    const int Backups = 5;
    /// <summary>Daily files above this size continue in _2, _3…; the whole folder is kept under <see cref="FolderBytes"/>.</summary>
    const long DailyBytes = 100L << 20, FolderBytes = 1L << 30;
    const int KeepDays = 30;
    static readonly Dictionary<string, (string date, int part, string file)> Daily = new();
    static string lastCleanup = "";

    public static string? Folder { get { lock (Gate) return folder; } }

    public static void Configure(string file)
    {
        lock (Gate) path = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    }

    /// <summary>Starts the daily files in <paramref name="directory"/> (created if missing); returns false when it cannot be written.</summary>
    public static bool ConfigureFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { return false; }
        lock (Gate) { folder = directory; Daily.Clear(); lastCleanup = ""; }
        return true;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARNING", message);
    public static void Error(string message, Exception? exc = null) => Write("ERROR", exc == null ? message : message + "\n" + exc);

    /// <summary>One line of the status file (already formatted by <see cref="StatusLog"/>).</summary>
    public static void Status(string line)
    {
        lock (Gate)
        {
            try { AppendDaily("stato", Stamp(DateTime.Now) + " " + line + "\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string Stamp(DateTime now) =>
        now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," + now.Millisecond.ToString("000", CultureInfo.InvariantCulture);

    static void Write(string level, string message)
    {
        lock (Gate)
        {
            var line = Stamp(DateTime.Now) + " " + level + " root " + message + "\n";
            if (path != null)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length + Encoding.UTF8.GetByteCount(line) > MaxBytes) Rotate();
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try
            {
                AppendDaily("funzionamento", line);
                if (level != "INFO") AppendDaily("errori", line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Appends to today's file of a kind (caller holds the lock).</summary>
    static void AppendDaily(string kind, string text)
    {
        if (folder == null) return;
        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (date != lastCleanup) { lastCleanup = date; Cleanup(); }
        if (!Daily.TryGetValue(kind, out var current) || current.date != date) current = (date, 1, DailyFile(kind, date, 1));
        if (File.Exists(current.file) && new FileInfo(current.file).Length > DailyBytes)
        {
            var part = current.part;
            string next;
            do next = DailyFile(kind, date, ++part); while (File.Exists(next) && new FileInfo(next).Length > DailyBytes);
            current = (date, part, next);
        }
        Daily[kind] = current;
        File.AppendAllText(current.file, text, new UTF8Encoding(false));
    }

    static string DailyFile(string kind, string date, int part) =>
        Path.Combine(folder!, $"SFA_{kind}_{date}" + (part > 1 ? "_" + part.ToString(CultureInfo.InvariantCulture) : "") + ".log");

    /// <summary>Removes files older than 30 days, then the oldest ones while the folder is above 1 GiB.</summary>
    static void Cleanup()
    {
        try
        {
            var files = new DirectoryInfo(folder!).EnumerateFiles("SFA_*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
            var limit = DateTime.UtcNow.AddDays(-KeepDays);
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc >= limit && total <= FolderBytes) break;
                total -= file.Length;
                try { file.Delete(); } catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
    }

    static void Rotate()
    {
        for (var i = Backups - 1; i >= 1; i--)
        {
            var source = path + "." + i;
            if (File.Exists(source)) File.Move(source, path + "." + (i + 1), true);
        }
        File.Move(path!, path + ".1", true);
    }
}
