using System.Reflection;
using Microsoft.Toolkit.Uwp.Notifications;

namespace WebFilter.Notifier.Tests;

/// <summary>The command line, and what each start does — also when Windows starts the notifier for a click on a toast.</summary>
public sealed class NotifierArgumentsTests
{
    [Theory]
    [InlineData(new string[0], "WebFilter.Notify")]
    [InlineData(new[] { "--pipe", "WebFilter.Test.Notify" }, "WebFilter.Test.Notify")]
    [InlineData(new[] { "--PIPE=WebFilter.Sim.Notify" }, "WebFilter.Sim.Notify")]
    [InlineData(new[] { "--pipe" }, "WebFilter.Notify")]
    public void The_pipe_can_be_named_for_a_development_service(string[] args, string pipe) =>
        Assert.Equal(pipe, NotifierArguments.Parse(args).Pipe);

    [Fact]
    public void Windows_marks_a_start_for_a_toast_click_and_COM_adds_its_own_flag()
    {
        // The command line the toolkit registers (LocalServer32), and what COM appends to it.
        var arguments = NotifierArguments.Parse(["-ToastActivated", "-Embedding"]);

        Assert.Equal(new NotifierArguments("WebFilter.Notify", StartedByClick: true, Unregister: false), arguments);
        Assert.Equal(new NotifierArguments("WebFilter.Notify", false, false), NotifierArguments.Parse([]));
        Assert.True(NotifierArguments.Parse(["--UNREGISTER"]).Unregister);
    }

    [Fact]
    public void The_click_flag_is_the_one_the_toolkit_registers()
    {
        // Read from the assembly's metadata: the class's static constructor, which registers the app, does not run.
        var field = typeof(ToastNotificationManagerCompat).GetField("TOAST_ACTIVATED_LAUNCH_ARG", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(NotifierArguments.ToastActivated, field.GetRawConstantValue());
    }

    [Theory]
    [InlineData(new string[0], true, NotifierStart.Tray)]
    [InlineData(new string[0], false, NotifierStart.Exit)]
    // No notifier was running: this one takes the click, then stays as the session's notifier.
    [InlineData(new[] { "-ToastActivated", "-Embedding" }, true, NotifierStart.Tray)]
    // One runs but had not registered for clicks yet (Windows would have sent the click to it): open here, then exit.
    [InlineData(new[] { "-ToastActivated", "-Embedding" }, false, NotifierStart.OpenAdmin)]
    [InlineData(new[] { "--unregister" }, true, NotifierStart.Unregister)]
    [InlineData(new[] { "--unregister" }, false, NotifierStart.Unregister)]
    public void Each_start_does_one_thing(string[] args, bool firstInSession, NotifierStart start) =>
        Assert.Equal(start, NotifierArguments.Parse(args).Start(firstInSession));
}
