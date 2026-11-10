using System.Globalization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using GenderValue = OMTS.Domain.Enums.Gender;

namespace OMTS.Application.Features.Employees;

/// <summary>
/// The columns of the employee file (export, template and import share them, matched by header) and how the cell texts map to
/// the values of a profile. Headers and labels are Vietnamese, as the people who fill the file read them; the English
/// spellings of the type and gender are accepted on import too.
/// </summary>
internal static class EmployeeFileColumns
{
    public const string Code = "Mã nhân viên";
    public const string Name = "Họ và tên";
    public const string Phone = "Số điện thoại";
    public const string Birthday = "Ngày sinh";
    public const string Type = "Loại nhân viên";
    public const string Zones = "Vùng làm việc";
    public const string JobTypes = "Loại công việc";
    public const string Address = "Địa chỉ";
    public const string Area = "Khu vực";
    public const string Ward = "Phường/Xã";
    public const string Notes = "Ghi chú";
    public const string Bank = "Ngân hàng";
    public const string AccountNumber = "Số tài khoản";
    public const string AccountHolder = "Chủ tài khoản";
    public const string Gender = "Giới tính";
    public const string IdNumber = "Số CCCD";
    public const string Email = "Email";
    public const string StartDate = "Ngày bắt đầu làm việc";
    public const string Status = "Tình trạng";
    public const string LastWorkingDay = "Ngày làm cuối";

    /// <summary>The first thirteen are those of EMP-API-04; the last four are the optional profile fields of EMP-API-06.</summary>
    public static readonly string[] Import =
    [
        Name, Phone, Birthday, Type, Zones, JobTypes, Address, Area, Ward, Notes, Bank, AccountNumber, AccountHolder,
        Code, Gender, IdNumber, Email, StartDate,
    ];

    public static readonly string[] RequiredOnImport = [Name, Type, Zones];

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss"];

    public static string TypeLabel(EmployeeType? type) => type switch
    {
        EmployeeType.SeasonalWorker => "Thời vụ",
        EmployeeType.Staff => "Nhân viên",
        _ => string.Empty,
    };

    public static string GenderLabel(GenderValue? gender) => gender switch
    {
        GenderValue.Male => "Nam",
        GenderValue.Female => "Nữ",
        GenderValue.Other => "Khác",
        _ => string.Empty,
    };

    public static string StatusLabel(int statusId) => statusId == StatusIds.Employee.Working ? "Đang làm việc" : "Đã nghỉ việc";

    /// <summary>The type of a cell; null when it is empty or not a type.</summary>
    public static EmployeeType? ParseType(string? text) => Normalize(text) switch
    {
        "thoi vu" or "seasonalworker" or "seasonal worker" => EmployeeType.SeasonalWorker,
        "nhan vien" or "staff" => EmployeeType.Staff,
        _ => null,
    };

    /// <param name="valid">False when the cell has text that is not a gender.</param>
    public static GenderValue? ParseGender(string? text, out bool valid)
    {
        valid = true;
        var gender = Normalize(text) switch
        {
            "" => (GenderValue?)null,
            "nam" or "male" => GenderValue.Male,
            "nu" or "female" => GenderValue.Female,
            "khac" or "other" => GenderValue.Other,
            _ => null,
        };
        valid = gender is not null || string.IsNullOrWhiteSpace(text);
        return gender;
    }

    /// <param name="valid">False when the cell has text that is not a date.</param>
    public static DateOnly? ParseDate(string? text, out bool valid)
    {
        valid = true;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return DateOnly.FromDateTime(date);
        }

        valid = false;
        return null;
    }

    /// <summary>The items of a cell such as "VUNG01, VUNG02" (comma or semicolon), trimmed, without empty ones.</summary>
    public static List<string> Split(string? text) =>
        (text ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>A phone typed in a number cell loses its leading zero in the spreadsheet.</summary>
    public static string? RestoredPhone(string? phone) => phone is { Length: 9 } && phone.All(char.IsAsciiDigit) ? $"0{phone}" : phone;

    /// <summary>Lower case without accents, so "Thời vụ", "thoi vu" and "THỜI VỤ" read the same.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var plain = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return plain.Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();
    }
}
