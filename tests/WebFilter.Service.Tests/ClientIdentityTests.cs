using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WebFilter.Service.Pipes;

namespace WebFilter.Service.Tests;

/// <summary>
/// The pipe client's identity. In the lab every standard account's native host failed with "Access is denied" in
/// <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/>: the token was opened with TOKEN_QUERY only, and the
/// administrator check duplicates it. Administrators (split token) never reach that check, so tests run as one passed.
/// </summary>
public sealed class ClientIdentityTests
{
    [Fact]
    public void The_administrator_check_works_on_a_token_opened_the_way_the_service_opens_it()
    {
        using var process = Process.GetCurrentProcess();
        using var token = ClientIdentity.OpenToken(process.SafeHandle);
        Assert.NotNull(token);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());

        var isAdmin = ClientIdentity.IsAdministrator(identity); // must not throw, whatever the answer
        Assert.Equal(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), isAdmin);
    }

    [Fact]
    public void With_TOKEN_QUERY_alone_the_administrator_check_is_refused_which_is_why_the_service_asks_for_more()
    {
        using var process = Process.GetCurrentProcess();
        Assert.True(OpenProcessToken(process.SafeHandle, TokenQuery, out var token));
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            Assert.Throws<SecurityException>(() => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }
    }

    [Fact]
    public async Task A_client_on_a_real_pipe_is_identified_by_the_OS()
    {
        var name = $"WebFilter.Test.Identity.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));

        using var identity = ClientIdentity.Of(server);

        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.Equal(WindowsIdentity.GetCurrent().User?.Value, identity.UserSid);
        Assert.NotEqual("unknown", identity.Elevation);
    }

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}
