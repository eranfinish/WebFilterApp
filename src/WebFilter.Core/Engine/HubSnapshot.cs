using WebFilter.Core.Model;
using WebFilter.Core.Time;

namespace WebFilter.Core.Engine;

/// <summary>What an agent receives from the hub: the settings bundle, parent-set runtime state and the shared usage.</summary>
public sealed record HubSnapshot(int Version, FamilyConfig Config, IReadOnlyDictionary<string, MemberRuntime> Runtime, UsageLedger Usage);

public static class ConfigCloning
{
    /// <summary>Deep enough copy for a snapshot: new lists; the records inside are immutable.</summary>
    public static FamilyConfig Clone(this FamilyConfig config) => new()
    {
        TimeZone = config.TimeZone,
        Members = [.. config.Members],
        Devices = [.. config.Devices],
        Categories = new Dictionary<string, IReadOnlyList<string>>(config.Categories, StringComparer.OrdinalIgnoreCase),
        MemberCategories = [.. config.MemberCategories],
        Rules = [.. config.Rules],
        Schedules = [.. config.Schedules],
        DailyLimits = [.. config.DailyLimits],
        YouTubeChannels = [.. config.YouTubeChannels],
        SupportedBrowsers = [.. config.SupportedBrowsers],
        AppRules = [.. config.AppRules],
        WindowsFeatures = config.WindowsFeatures,
        Baseline = config.Baseline,
    };
}
