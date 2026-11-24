using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>Vietnamese MSG-GEN-09 style messages of the employee profile that name the days concerned (L16); the values are also in <see cref="UserMessage.Parameters"/>.</summary>
public static class EmployeeEndingMessages
{
    private const int MaxDatesShown = 8;

    /// <summary>A working zone cannot be taken off a profile while assignments from today on remain in it.</summary>
    public static UserMessage ZoneHasAssignments(string employeeName, string zoneCode, IReadOnlyList<DateOnly> dates) => new(
        MessageCodes.Gen09,
        $"Không thể gỡ vùng {zoneCode} khỏi hồ sơ {employeeName}: còn phân công tại vùng này vào các ngày {DateList(dates)}. Hãy xóa hoặc đổi các phân công đó trước.",
        MessageCodes.Legacy.Msg61)
    {
        Parameters = new Dictionary<string, object?> { ["employee_name"] = employeeName, ["zone_code"] = zoneCode, ["dates"] = dates },
    };

    /// <summary>"12/11, 13/11 và 3 ngày khác" for the days given, oldest first.</summary>
    public static string DateList(IReadOnlyList<DateOnly> dates)
    {
        var shown = string.Join(", ", dates.Distinct().Order().Take(MaxDatesShown).Select(d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)));
        var more = dates.Distinct().Count() - MaxDatesShown;
        return more > 0 ? $"{shown} và {more} ngày khác" : shown;
    }
}
