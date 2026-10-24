using System.IO.Pipes;
using System.Security.Principal;
using WebFilter.NativeHost;

namespace WebFilter.Service.Tests;

/// <summary>
/// The native host trusts the agent pipe by its owner. It used to open the server's token, which a standard account may
/// not do for a LocalSystem process, so every child's host refused the real service (the lab, 2026-10-24).
/// </summary>
public sealed class PipeServerCheckTests
{
    [Fact]
    public void Only_LocalSystem_or_the_Administrators_group_are_trusted_owners()
    {
        Assert.True(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(PipeServerCheck.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(PipeServerCheck.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.False(PipeServerCheck.IsTrustedOwner(null));
    }

    [Fact]
    public async Task The_client_reads_the_owner_of_a_pipe_it_did_not_create_with_its_ordinary_handle()
    {
        var name = $"WebFilter.Test.Owner.{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(5000));

        // This process created the pipe, so its owner is this token's default owner: the user, or the Administrators group
        // in an elevated process. Unelevated (as the tests run), it is not trusted — exactly a child's own fake server.
        var owner = PipeServerCheck.OwnerOf(client);
        Assert.Equal(WindowsIdentity.GetCurrent().Owner, owner);
        var settings = new HostSettings("unused", "unused", RequireSystemServer: true);
        Assert.Equal(PipeServerCheck.IsTrustedOwner(owner), PipeServerCheck.IsTrusted(client, settings));
        Assert.True(PipeServerCheck.IsTrusted(client, settings with { RequireSystemServer = false }));
    }
}
