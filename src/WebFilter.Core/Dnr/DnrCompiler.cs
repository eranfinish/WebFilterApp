using WebFilter.Core.Model;

namespace WebFilter.Core.Dnr;

/// <summary>
/// Compiles a member's <see cref="EffectiveUrlPolicy"/> into declarativeNetRequest rules whose outcome (allow vs block)
/// equals <see cref="Urls.RuleEvaluator"/> for every URL, plus the YouTube-mode and IP-literal rules.
/// Only main_frame and sub_frame are filtered. Blocks redirect to the block page with the original URL.
/// See spikes/S3-S4/REPORT.md for what DNR supports and its limits.
/// </summary>
public static class DnrCompiler
{
    public static DnrRuleSet Compile(EffectiveUrlPolicy policy, DnrCompileOptions options) => throw new NotImplementedException();
}
