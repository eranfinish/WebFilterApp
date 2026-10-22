using System.Globalization;
using System.IO;
using System.Text;

namespace WebFilter.Notifier;

/// <summary>Where the notifier keeps its files: <c>%LOCALAPPDATA%\WebFilter\</c>, the user's own folder.</summary>
public static class NotifierPaths
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebFilter");

    /// <summary>The <c>at</c> of the last notification this user was shown.</summary>
    public static string Cursor => Path.Combine(Folder, "notifier-cursor.txt");

    public static string Log => Path.Combine(Folder, "notifier.log");
}

/// <summary>The position in the hub's notifications, per user (PROTECTION-SPEC §10). Replaced by a fake in tests.</summary>
public interface ICursorStore
{
    /// <summary>The <c>at</c> of the last notification shown; null when there is none yet (the first run).</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file does not hold a time.</exception>
    DateTimeOffset? Load();

    /// <exception cref="IOException">The file could not be written.</exception>
    void Save(DateTimeOffset at);
}

/// <summary>One line, the instant in round-trip format, written through a temporary file so a crash never leaves half of it.</summary>
public sealed class FileCursorStore(string path) : ICursorStore
{
    public string Path { get; } = path;

    public DateTimeOffset? Load()
    {
        string text;
        try
        {
            text = File.ReadAllText(Path, Encoding.UTF8).Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        return DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : throw new InvalidDataException($"{Path} does not hold a time: '{text}'.");
    }

    public void Save(DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, at.ToString("O", CultureInfo.InvariantCulture), Encoding.UTF8);
            File.Move(temporary, Path, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}

/// <summary>What the notifier does not show the user: refusals, unreadable replies, a missing pipe. Never throws.</summary>
public interface INotifierLog
{
    void Write(string message);
}

/// <summary>Appends to a text file next to the cursor; past <see cref="MaxBytes"/> the file is moved to <c>.old</c> and started again.</summary>
public sealed class FileNotifierLog(string path, TimeProvider? clock = null) : INotifierLog
{
    public const long MaxBytes = 256 * 1024;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it; the notifier goes on.
            }
        }
    }
}
