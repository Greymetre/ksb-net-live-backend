using System.Globalization;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Field-app (SFA) notifications for attendance and leave decisions, sent to the employee
/// the record belongs to. Sent after the approval has been saved and the response returned,
/// on a scope of their own: approving never waits on Firebase, and a push that fails is only
/// logged. A tap opens the employee's attendance report.
/// </summary>
public sealed class HrPushNotifications
{
    private const string AttendanceScreen = "AttendanceReport";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HrPushNotifications> _logger;

    public HrPushNotifications(IServiceScopeFactory scopeFactory, ILogger<HrPushNotifications> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>"Leave approved" to the employee who applied.</summary>
    public void LeaveApproved(ulong leaveId) => Run(async (db, push) =>
    {
        var leave = await db.Leaves.AsNoTracking().Where(x => x.Id == leaveId && x.Status == 1)
            .Select(x => new { x.UserId, x.FromDate, x.ToDate, x.Type }).FirstOrDefaultAsync();
        if (leave?.UserId is not { } userId) return;
        var kind = LeaveKind(leave.Type);
        await push.SendToUserAsync(userId, "Leave approved ✅",
            $"Your {kind} for {Period(leave.FromDate, leave.ToDate)} has been approved.",
            Target("leave", leaveId));
    });

    /// <summary>"Attendance approved" to each employee whose attendance was approved - one
    /// notification per employee, however many days were approved together.</summary>
    public void AttendanceApproved(IReadOnlyCollection<ulong> attendanceIds) => Run(async (db, push) =>
    {
        if (attendanceIds.Count == 0) return;
        var ids = attendanceIds.ToArray();
        var rows = await db.Attendances.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.AttendanceStatus == 1 && x.UserId != null)
            .Select(x => new { x.Id, UserId = x.UserId!.Value, x.PunchinDate }).ToListAsync();
        foreach (var group in rows.GroupBy(x => x.UserId))
        {
            var days = group.Select(x => x.PunchinDate.Date).Where(x => x.Year > 2000).Distinct().OrderBy(x => x).ToList();
            var body = days.Count switch
            {
                0 => "Your attendance has been approved.",
                1 => $"Your attendance for {Day(days[0])} has been approved.",
                _ => $"Your attendance for {days.Count} days ({Day(days[0])} – {Day(days[^1])}) has been approved."
            };
            await push.SendToUserAsync(group.Key, "Attendance approved ✅", body, Target("attendance", group.First().Id));
        }
    });

    private static Dictionary<string, string> Target(string type, ulong id) => new()
    {
        ["type"] = type,
        ["id"] = id.ToString(CultureInfo.InvariantCulture),
        ["screen"] = AttendanceScreen
    };

    private static string LeaveKind(string? type)
    {
        var text = (type ?? string.Empty).Trim();
        if (text.Length == 0 || text.Equals("leave", StringComparison.OrdinalIgnoreCase)) return "leave";
        return text.Contains("leave", StringComparison.OrdinalIgnoreCase) ? text.ToLowerInvariant() : $"{text.ToLowerInvariant()} leave";
    }

    private static string Day(DateTime date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Period(DateTime from, DateTime to) =>
        from.Date == to.Date ? Day(from) : $"{Day(from)} – {Day(to)}";

    private void Run(Func<AppDbContext, PushNotificationService, Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var push = scope.ServiceProvider.GetRequiredService<PushNotificationService>();
            if (!push.IsConfigured) return;
            await work(db, push);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "HR push notification failed");
        }
    });
}
