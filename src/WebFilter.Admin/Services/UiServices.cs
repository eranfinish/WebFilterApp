using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace WebFilter.Admin.Services;

/// <summary>Periodic work on the UI thread (view models stay free of WPF types and testable with a manual timer).</summary>
public interface IUiTimer
{
    /// <summary>Calls <paramref name="tick"/> every <paramref name="interval"/>, skipping a tick while the previous one still runs. Dispose to stop.</summary>
    IDisposable Start(TimeSpan interval, Func<Task> tick);
}

/// <summary>Questions that need the parent's answer before an irreversible action.</summary>
public interface IDialogService
{
    bool Confirm(string message, string title);
}

/// <summary>Asks the parent where to save or which file to open (view models stay testable without the Windows dialogs).</summary>
public interface IFileDialogService
{
    /// <summary>The path chosen to save to, or null when the parent cancelled.</summary>
    string? ChooseSaveFile(string suggestedName, string title);

    /// <summary>The path of the file chosen to open, or null when the parent cancelled.</summary>
    string? ChooseOpenFile(string title);

    /// <summary>The folder chosen (the dialog can create one), or null when the parent cancelled.</summary>
    string? ChooseFolder(string title, string? initialFolder);
}

/// <summary>Reads and writes the files the parent chose (in memory in tests).</summary>
public interface IFileStore
{
    /// <exception cref="IOException">The file could not be written.</exception>
    void WriteAllText(string path, string text);

    /// <exception cref="IOException">The file could not be read.</exception>
    string ReadAllText(string path);
}

/// <summary>Puts text on the Windows clipboard (view models stay testable without one).</summary>
public interface IClipboardService
{
    /// <summary>False when the clipboard could not be opened (another program holds it); nothing was copied.</summary>
    bool TrySetText(string text);
}

public sealed class DispatcherUiTimer : IUiTimer
{
    public IDisposable Start(TimeSpan interval, Func<Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        var running = false;
        timer.Tick += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await tick();
            }
            finally
            {
                running = false;
            }
        };
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

public sealed class MessageBoxDialogService : IDialogService
{
    public bool Confirm(string message, string title)
    {
        const MessageBoxOptions rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
        var owner = Application.Current?.MainWindow;
        var answer = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, rtl)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, rtl);
        return answer == MessageBoxResult.Yes;
    }
}

public sealed class WpfFileDialogService : IFileDialogService
{
    private const string Filter = "WebFilter backup (*.json)|*.json";

    public string? ChooseSaveFile(string suggestedName, string title)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = Filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseOpenFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = Filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ChooseFolder(string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initialFolder is not null && System.IO.Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }
}

public sealed class DiskFileStore : IFileStore
{
    public void WriteAllText(string path, string text)
    {
        try
        {
            System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new System.IO.IOException(ex.Message, ex);
        }
    }

    public string ReadAllText(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new System.IO.IOException(ex.Message, ex);
        }
    }
}

public sealed class WpfClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            // CLIPBRD_E_CANT_OPEN: another program has the clipboard open.
            return false;
        }
    }
}
