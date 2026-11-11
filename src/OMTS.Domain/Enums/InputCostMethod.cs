namespace OMTS.Domain.Enums;

/// <summary>How the cost of an input item is kept: Fixed stays what the user set (the first receipt price when none was set), Average is a moving average over receipts and returns.</summary>
public enum InputCostMethod
{
    Fixed,
    Average,
}
