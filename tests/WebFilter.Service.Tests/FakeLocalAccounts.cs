using WebFilter.Core.Model;
using WebFilter.Service.Accounts;

namespace WebFilter.Service.Tests;

/// <summary>The Windows accounts a test says this computer has (tests never depend on the real machine's accounts).</summary>
internal sealed class FakeLocalAccounts : ILocalAccounts
{
    private readonly object _gate = new();
    private List<DeviceAccount> _seen = [];

    public FakeLocalAccounts(params DeviceAccount[] accounts) => _seen = [.. accounts];

    /// <summary>Replaces the accounts (the service may read them on another thread).</summary>
    public void Set(params DeviceAccount[] accounts)
    {
        lock (_gate) _seen = [.. accounts];
    }

    public void Add(DeviceAccount account)
    {
        lock (_gate) _seen = [.. _seen, account];
    }

    public IReadOnlyList<DeviceAccount> List()
    {
        lock (_gate) return [.. _seen];
    }
}
