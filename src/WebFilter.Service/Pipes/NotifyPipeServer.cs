using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Framing;
using WebFilter.Contracts.Notify;

namespace WebFilter.Service.Pipes;

/// <summary>
/// <c>\\.\pipe\WebFilter.Notify</c> on the hub (PROTECTION-SPEC §10): the notifier app, which runs unelevated in every
/// signed-in user's session, polls it for parent notifications. Read only. The DACL lets any signed-in user open the pipe,
/// so the notifier needs no elevation; the server then answers only clients whose token the OS reports as an
/// administrator's, elevated or not (<see cref="ClientIdentity.IsAdminAccount"/>) — a child's process gets
/// <see cref="NotifyError.NotAdmin"/> and the pipe is closed. Not served on agent computers.
/// </summary>
public sealed class NotifyPipeServer(FamilyService family, IOptions<ServiceOptions> options, ILogger<NotifyPipeServer> log) : BackgroundService
{
    private readonly ServiceOptions _options = options.Value;

    /// <summary>How long a refused client has to send its first request, whose id the refusal then carries.</summary>
    internal static readonly TimeSpan RefusalWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Who is answered, from the OS-reported identity. Always <see cref="ClientIdentity.IsAdminAccount"/> in the service;
    /// tests replace it to reach the refusal from a process that is an administrator (a second, standard account is not
    /// available to a test run).
    /// </summary>
    internal Func<ClientIdentity, bool> IsAllowed { get; init; } = static client => client.IsAdminAccount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!family.IsHub) return;
        log.LogInformation("Notify pipe listening on {Pipe}", _options.NotifyPipeName);
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // The first instance must be ours, so another process cannot pose as the service to the notifier.
                pipe = NamedPipeServerStreamAcl.Create(_options.NotifyPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, Security());
                first = false;
            }
            catch (Exception ex) when (first && ex is UnauthorizedAccessException or IOException)
            {
                log.LogCritical(ex, "The notify pipe name {Pipe} is already taken by another process; retrying", _options.NotifyPipeName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(pipe, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Notify pipe session failed"); }
            }, stoppingToken);
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
            log.LogWarning(ex, "Could not identify a notify pipe client");
            return;
        }
        using (client)
        {
            if (!IsAllowed(client))
            {
                log.LogWarning("Rejected a notify pipe client that is not an administrator: {Client}", client);
                await RefuseAsync(pipe, ct);
                return;
            }
            try
            {
                while (await MessageFraming.ReadAsync(pipe, ct) is { } frame)
                {
                    var (request, id) = Parse(frame);
                    NotifyMessage reply = request switch
                    {
                        GetNotifications get => new NotificationsResult(family.NotificationsSince(get.Since)),
                        _ => new NotifyError("unsupported", "Only getNotifications is served here."),
                    };
                    await NotifyProtocol.WriteAsync(pipe, reply with { Id = id }, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
            }
        }
    }

    /// <summary>A frame that is not a known message (a newer notifier, garbage) is answered "unsupported", not fatal.</summary>
    private static (NotifyMessage? Message, string? Id) Parse(byte[] frame)
    {
        try
        {
            var message = NotifyProtocol.Deserialize(frame);
            return (message, message.Id);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            try
            {
                using var doc = JsonDocument.Parse(frame);
                return (null, doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() : null);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }

    /// <summary>Answers the client's first request (or, after <see cref="RefusalWait"/>, nothing) with the refusal, then hangs up.</summary>
    private static async Task RefuseAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? firstId = null;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(RefusalWait);
            if (await MessageFraming.ReadAsync(pipe, wait.Token) is { } frame) firstId = Parse(frame).Id;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
        try { await NotifyProtocol.WriteAsync(pipe, new NotifyError(NotifyError.NotAdmin, "Notifications are for the parents' (administrator) accounts.") { Id = firstId }, ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>Any signed-in user may connect (the server checks who); SYSTEM, administrators and the service account own the pipe.</summary>
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
