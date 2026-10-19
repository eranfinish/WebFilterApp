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
/// </summary>
internal sealed class Fallback(HostSettings settings)
{
    public async Task RunAsync(Stream stdin, Stream stdout)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? "";
        var isAdmin = IsAdminAccount(identity);
        var cache = ReadCache(sid);
        var restricted = !isAdmin && (cache?.Restricted ?? true);
        string? extensionId = null;

        while (await AgentProtocol.ReadAsync(stdin) is { } message)
        {
            AgentMessage reply = message switch
            {
                HelloRequest hello => Hello(hello, out extensionId),
                GetRulesRequest => Rules(),
                HeartbeatRequest => new TimeStateMessage("unlimited", null, null),
                RequestAccessRequest => new RequestAccessResponse("unavailable", null),
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
