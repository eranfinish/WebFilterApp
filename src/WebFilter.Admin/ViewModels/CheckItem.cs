using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// One requirement check (<see cref="GetDiagnostics"/>) in words: "Check_‹id›_‹state›" in Strings.resx, or
/// "Check_‹id›_‹detail›" for a state whose detail is a code of its own (onedrive ok with "none": not installed).
/// </summary>
public sealed record CheckItem(string State, string Text)
{
    /// <summary>The onedrive check's detail when OneDrive is not installed at all (MachineChecks.OneDriveNone).</summary>
    public const string OneDriveNone = "none";

    public bool IsOk => State == "ok";

    public bool IsFail => State == "fail";

    /// <summary>✓ ok, ! warning, ✕ failure.</summary>
    public string Glyph => State switch { "ok" => "✓", "fail" => "✕", _ => "!" };

    public static CheckItem From(CheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var detail = check.Id == "browsers"
            ? string.Join(", ", (check.Detail ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(b => b switch { "edge" => "Edge", "chrome" => "Chrome", _ => b }))
            : check.Detail;
        var key = check is { Id: "onedrive", State: "ok", Detail: OneDriveNone } ? "Check_onedrive_none" : $"Check_{check.Id}_{check.State}";
        // A check this app does not know yet (a newer service) is shown by its codes rather than hidden.
        var format = Strings.ResourceManager.GetString(key, Strings.Culture);
        return new CheckItem(check.State, format is null ? $"{check.Id}: {check.State} {detail}".Trim() : Texts.Format(format, Texts.Ltr(detail)));
    }

    /// <summary>The checks of the computer the app talks to; none from a service that does not know the message.</summary>
    public static async Task<IReadOnlyList<CheckItem>> LoadAsync(IAdminClient client)
    {
        try
        {
            return [.. (await client.RequestAsync<DiagnosticsResult>(new GetDiagnostics())).Checks.Select(From)];
        }
        catch (AdminServerException)
        {
            return [];
        }
    }
}
