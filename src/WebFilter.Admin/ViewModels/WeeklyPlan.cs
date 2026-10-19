using System.Globalization;
using System.Text.RegularExpressions;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>A local time of day as the settings write it: "HH:MM", with "24:00" for the end of the day.</summary>
internal static partial class ClockTime
{
    public const int EndOfDay = 24 * 60;

    /// <summary>Accepts "9:30", "09:30" and, when <paramref name="allowEndOfDay"/>, "24:00"; returns minutes since midnight.</summary>
    public static bool TryParse(string? text, bool allowEndOfDay, out int minute)
    {
        minute = 0;
        var match = Pattern().Match(text ?? "");
        if (!match.Success) return false;
        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (minutes > 59 || hours > 24 || (hours == 24 && (minutes > 0 || !allowEndOfDay))) return false;
        minute = hours * 60 + minutes;
        return true;
    }

    public static string Format(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    [GeneratedRegex(@"^\s*(\d{1,2}):(\d{2})\s*$")]
    private static partial Regex Pattern();
}

/// <summary>One day of a member's week: the hours browsing is allowed (none = not at all, when hours are restricted) and the daily limit.</summary>
internal sealed record DayPlan(int Day, IReadOnlyList<(int Start, int End)> Windows, int? LimitMinutes);

/// <summary>
/// A member's <see cref="ScheduleDto"/> and <see cref="DailyLimitDto"/> entries as seven days, Sunday first, and back.
/// Same meaning as the engine (tests/simulation/README.md): no schedule at all = any hour; with schedules, a day without
/// a window has no browsing hours; with several limits on one day the smallest counts.
/// </summary>
internal static class WeeklyPlan
{
    public const int Days = 7;

    /// <summary>Whether the member's hours are restricted at all, and each day's windows (sorted) and limit.</summary>
    public static (bool RestrictsHours, IReadOnlyList<DayPlan> Days) From(FamilyBundle settings, string memberId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var schedules = settings.Schedules.Where(s => s.Member == memberId).ToList();
        var limits = settings.DailyLimits.Where(l => l.Member == memberId).ToList();
        var days = Enumerable.Range(0, Days).Select(day => new DayPlan(day,
                schedules.Where(s => s.Days.Contains(day))
                    .Select(s => (Start: Minute(s.Start), End: Minute(s.End)))
                    .Distinct().OrderBy(w => w.Start).ThenBy(w => w.End).ToList(),
                limits.Where(l => l.Days.Contains(day)).Select(l => (int?)l.Minutes).Min()))
            .ToList();
        return (schedules.Count > 0, days);
    }

    /// <summary>The entries for <paramref name="days"/>, with the days that share a window or a limit in one entry.</summary>
    public static (List<ScheduleDto> Schedules, List<DailyLimitDto> Limits) To(string memberId, bool restrictsHours, IEnumerable<DayPlan> days)
    {
        var list = days.ToList();
        var schedules = restrictsHours
            ? list.SelectMany(d => d.Windows.Select(w => (d.Day, w.Start, w.End)))
                .GroupBy(x => (x.Start, x.End))
                .Select(g => (Days: g.Select(x => x.Day).Distinct().Order().ToList(), g.Key.Start, g.Key.End))
                .OrderBy(x => x.Days[0]).ThenBy(x => x.Start)
                .Select(x => new ScheduleDto(memberId, x.Days, ClockTime.Format(x.Start), ClockTime.Format(x.End)))
                .ToList()
            : [];
        var limits = list.Where(d => d.LimitMinutes is not null)
            .GroupBy(d => d.LimitMinutes!.Value)
            .Select(g => new DailyLimitDto(memberId, g.Select(d => d.Day).Order().ToList(), g.Key))
            .OrderBy(l => l.Days[0])
            .ToList();
        return (schedules, limits);
    }

    /// <summary>Settings with <paramref name="memberId"/>'s hours and limits replaced; everyone else's stay as they are.</summary>
    public static FamilyBundle Replace(FamilyBundle settings, string memberId, bool restrictsHours, IEnumerable<DayPlan> days)
    {
        var (schedules, limits) = To(memberId, restrictsHours, days);
        return settings with
        {
            Schedules = [.. settings.Schedules.Where(s => s.Member != memberId), .. schedules],
            DailyLimits = [.. settings.DailyLimits.Where(l => l.Member != memberId), .. limits],
        };
    }

    private static int Minute(string hhmm) =>
        ClockTime.TryParse(hhmm, allowEndOfDay: true, out var minute) ? minute : throw new FormatException($"Not a time of day: '{hhmm}'.");
}
