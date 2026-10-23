namespace OMTS.Domain.Enums;

/// <summary>How a work assignment is paid (E40, LAB-03): from attendance and the daily rate, or by the confirmed quantity.</summary>
public enum WorkPaymentType
{
    DailyWage,
    Piecework,
}
