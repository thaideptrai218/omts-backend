namespace OMTS.Domain.Enums;

/// <summary>
/// How a pay rate is paid (LAB-06): MonthlyFixed = the monthly amount in full; PerWorkingDay = monthly amount ×
/// paid days ÷ standard working days; DailyWage = the amount per paid day (the only type for seasonal workers).
/// </summary>
public enum SalaryType
{
    MonthlyFixed,
    PerWorkingDay,
    DailyWage,
}
