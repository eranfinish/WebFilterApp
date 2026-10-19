using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Options;

namespace WebFilter.Service.Pipes;

/// <summary>
/// <c>\\.\pipe\WebFilter.Agent</c>: any signed-in user may connect (the native host runs as the browser's user), but only
/// clients whose executable is a trusted native host get a session; identity comes from the OS (<see cref="ClientIdentity"/>).
/// </summary>
public sealed class AgentPipeServer(FamilyService family, IOptions<ServiceOptions> options, ILogger<AgentPipeServer> log) : BackgroundService
{
    private readonly ServiceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Agent pipe listening on {Pipe}", _options.AgentPipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(_options.AgentPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(() => ServeAsync(pipe, stoppingToken), stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var owned = pipe;
        ClientIdentity client;
        try
        {
            client = ClientIdentity.Of(pipe);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Could not identify an agent pipe client");
            return;
        }
        using (client)
        {
            if (!IsTrusted(client))
            {
                log.LogWarning("Rejected untrusted agent pipe client: {Client}", client);
                await Contracts.Agent.AgentProtocol.WriteAsync(pipe, new Contracts.Agent.ErrorMessage("untrusted_client", "This program may not use the agent pipe."), ct);
                return;
            }
            log.LogDebug("Agent session for {Client}", client);
            try
            {
                await new AgentSession(pipe, client, family, log).RunAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Agent session failed for {Client}", client);
            }
        }
    }

    private bool IsTrusted(ClientIdentity client) =>
        client.ImagePath is { } image && _options.TrustedClientPaths.Any(p => string.Equals(Path.GetFullPath(p), image, StringComparison.OrdinalIgnoreCase));

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
