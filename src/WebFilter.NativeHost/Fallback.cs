using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using WebFilter.Contracts.Agent;
using WebFilter.Core.Dnr;
using WebFilter.Core.Model;

namespace WebFilter.NativeHost;

/// <summary>
/// The host's own answers while the service is unreachable (README §9): administrators are unrestricted; restricted
/// accounts get only their always-allowed sites (from the profile cache); without a cache everything is blocked.
/// Meanwhile it checks whether the service is back, and exits when it is, so the extension reconnects to the service.
/// The parents' baseline protection (PROTECTION-SPEC §13) comes from the service only: administrators and unrestricted
/// members are answered "unrestricted" here as before it existed (no rules, no features), so a stopped service never
/// blocks a parent more than it used to; the baseline returns with the service.
/// </summary>
internal sealed class Fallback(HostSettings settings)
{
    /// <summary>How often the agent pipe is tried while the host answers by itself.</summary>
    private static readonly TimeSpan ServiceProbeInterval = TimeSpan.FromSeconds(3);

    /// <summary>Returns when the browser closes stdin, or when the service accepts connections again.</summary>
    public async Task RunAsync(Stream stdin, Stream stdout)
    {
        using var stop = new CancellationTokenSource();
        // Console stdin reads block their thread, so serve on the pool: the probe can end the host while a read waits.
        var serve = Task.Run(() => ServeAsync(stdin, stdout));
        var serviceBack = WaitForServiceAsync(stop.Token);
        var first = await Task.WhenAny(serve, serviceBack);
        await stop.CancelAsync();
        if (first == serve) await serve;
    }

    /// <summary>Completes once the agent pipe accepts a connection from a trusted server (the same check as at startup).</summary>
    private async Task WaitForServiceAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(ServiceProbeInterval, ct);
            await using var pipe = new NamedPipeClientStream(".", settings.AgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct);
                if (PipeServerCheck.IsTrusted(pipe, settings)) return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task ServeAsync(Stream stdin, Stream stdout)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? "";
        var isAdmin = !settings.SimulateStandardUser && IsAdminAccount(identity);
        var cache = ReadCache(sid);
        var restricted = !isAdmin && (cache?.Restricted ?? true);
        string? extensionId = null;

        while (await AgentProtocol.ReadLenientAsync(stdin) is { } message)
        {
            AgentMessage reply = message switch
            {
                HelloRequest hello => Hello(hello, out extensionId),
                GetRulesRequest => Rules(),
                HeartbeatRequest => new TimeStateMessage("unlimited", null, null),
                RequestAccessRequest => new RequestAccessResponse("unavailable", null),
                UnknownMessage unknown => new ErrorMessage("unsupported", $"Unknown message type '{unknown.Type}'."),
                _ => new ErrorMessage("service_unavailable", "The filtering service is not running."),
            };
            await AgentProtocol.WriteAsync(stdout, reply with { Id = message.Id });
        }

        HelloResponse Hello(HelloRequest hello, out string? id)
        {
            id = hello.ExtensionId;
            return new HelloResponse(new AgentUser(sid, identity.Name, isAdmin ? "admin" : "unknown", isAdmin),
                restricted ? "allowlist" : "unrestricted", "host", false, cache?.SettingsVersion ?? 0, typeof(Fallback).Assembly.GetName().Version?.ToString() ?? "0");
        }

        RulesResponse Rules()
        {
            var unlimited = new TimeStateMessage("unlimited", null, null);
            if (!restricted) return new RulesResponse(cache?.SettingsVersion ?? 0, [], [], new YouTubePolicy("unrestricted", [], []), unlimited);
            var always = (cache?.AlwaysAllowed ?? []).Select(r => new Rule(r.Id, r.Member, r.Pattern, Enum.Parse<PatternType>(r.Type, true), RuleAction.Allow, true, r.ExpiresAt)).ToList();
            var policy = new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], always, []);
            var compiled = DnrCompiler.Compile(policy, new DnrCompileOptions(extensionId ?? "unknown-extension-id"));
            return new RulesResponse(cache?.SettingsVersion ?? 0,
                compiled.DynamicRules.Select(r => JsonSerializer.SerializeToElement(r, DnrJsonContext.Default.DnrRule)).ToList(), [],
                new YouTubePolicy("off", [], []), unlimited);
        }
    }

    private ProfileCache? ReadCache(string sid)
    {
        try
        {
            var path = Path.Combine(settings.ProfileDirectory, ProfileCacheJson.FileName(sid));
            return File.Exists(path) ? ProfileCacheJson.Deserialize(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>An administrator account, elevated or not (spike S2: IsInRole is false for an unelevated admin).</summary>
    private static bool IsAdminAccount(WindowsIdentity identity)
    {
        if (GetTokenInformation(identity.Token, 18, out var type, sizeof(int), out _) && type is 2 or 3) return true;
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);
}
