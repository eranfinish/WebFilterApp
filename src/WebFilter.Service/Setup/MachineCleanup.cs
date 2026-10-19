using System.Security;
using System.Text.Json;
using WebFilter.Service.Network;
using WebFilter.Service.Policies;

namespace WebFilter.Service.Setup;

/// <summary>
/// What uninstall removes besides the files and keys the MSI owns (MVP-PLAN §4.6): every browser policy value the
/// service wrote — in HKLM and in every user's hive, loading the hives of users who are not logged on — our WFP filters,
/// and the data folder (settings, usage, events, pairing keys, profile caches). Every step runs even if an earlier one
/// failed; whatever could not be removed is reported. Run with the service stopped, or it writes the policies again.
/// </summary>
public sealed class MachineCleanup(IPolicyStore store, IUserHives hives, IWfpEngine? wfp, TextWriter log)
{
    /// <param name="PoliciesLeft">Registry paths of values that could not be removed.</param>
    /// <param name="WfpRemoved">False only when an engine exists and failed.</param>
    public sealed record Result(int PoliciesRemoved, IReadOnlyList<string> PoliciesLeft, bool WfpRemoved, bool DataDeleted)
    {
        public bool Complete => PoliciesLeft.Count == 0 && WfpRemoved && DataDeleted;
    }

    /// <param name="dataRoot">The folder to delete (<see cref="ServiceOptions.MachineDataRoot"/>).</param>
    /// <param name="policyStatePath">The <see cref="PolicyManager"/> state file — inside <paramref name="dataRoot"/>, so read first.</param>
    public Result Run(string dataRoot, string policyStatePath)
    {
        var (removed, left) = RemovePolicies(policyStatePath);
        var wfpRemoved = RemoveWfpFilters();
        var dataDeleted = DeleteData(dataRoot);
        return new Result(removed, left, wfpRemoved, dataDeleted);
    }

    private (int Removed, IReadOnlyList<string> Left) RemovePolicies(string statePath)
    {
        try
        {
            var manager = new PolicyManager(new HiveLoadingPolicyStore(store, hives), statePath);
            var removed = manager.RemoveAll();
            var left = manager.Remaining();
            log.WriteLine($"Browser policies: {removed} values removed, {left.Count} left.");
            foreach (var value in left) log.WriteLine($"  left: {value}");
            return (removed, left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            log.WriteLine($"Browser policies could not be removed: {ex.Message}");
            return (0, [statePath]);
        }
    }

    private bool RemoveWfpFilters()
    {
        if (wfp is null)
        {
            log.WriteLine("WFP: this build has no WFP engine; nothing to remove.");
            return true;
        }
        try
        {
            wfp.RemoveAll();
            log.WriteLine("WFP: provider, sublayer and filters removed.");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.WriteLine($"WFP filters could not be removed: {ex.Message}");
            return false;
        }
    }

    private bool DeleteData(string dataRoot)
    {
        try
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            log.WriteLine($"Data folder {dataRoot} deleted.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Data folder {dataRoot} could not be deleted: {ex.Message}");
            return false;
        }
    }
}
