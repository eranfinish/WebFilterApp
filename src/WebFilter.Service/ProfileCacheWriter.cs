using System.Security.AccessControl;
using System.Security.Principal;
using WebFilter.Contracts.Agent;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;

namespace WebFilter.Service;

/// <summary>
/// Writes one <see cref="ProfileCache"/> per account on this device for the native host to read while the service is
/// down. Each file is writable by the service only and readable by its own account (and administrators).
/// </summary>
public sealed class ProfileCacheWriter(string directory, ILogger log)
{
    public void Write(FamilyConfig config, string deviceId, int version, DateTimeOffset now)
    {
        var device = config.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null) return;
        Directory.CreateDirectory(directory);
        foreach (var account in device.Accounts)
        {
            var profile = Decider.ProfileFor(config, account, now);
            var cache = new ProfileCache(version, account.AccountId, profile.Restricted,
                profile.AlwaysAllowed.Select(r => new RuleDto(r.Id, r.MemberId, r.Pattern, r.Type.ToString().ToLowerInvariant(), r.Action.ToString().ToLowerInvariant(), r.AlwaysAllowed, r.ExpiresAt)).ToList());
            var path = Path.Combine(directory, ProfileCacheJson.FileName(account.AccountId));
            try
            {
                File.WriteAllText(path, ProfileCacheJson.Serialize(cache));
                Protect(path, account.AccountId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not write the profile cache for {Account}", account.AccountId);
            }
        }
    }

    private void Protect(string path, string accountId)
    {
        SecurityIdentifier reader;
        try { reader = new SecurityIdentifier(accountId); }
        catch (ArgumentException) { return; }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.Read, AccessControlType.Allow));
        try
        {
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IdentityNotMappedException or IOException)
        {
            log.LogDebug(ex, "Could not restrict access to {Path}", path);
        }
    }
}
