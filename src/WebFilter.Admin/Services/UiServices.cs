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
