namespace OMTS.Domain.Enums;

/// <summary>
/// The fixed group of a farming input item (BR-PURCHASE-10): Seed items are the seed bought for seeding and all use one
/// unit; Material items repair rafts (material usage, material cost of a raft); Other items are bought and paid only.
/// </summary>
public enum InputItemGroup
{
    Seed,
    Material,
    Other,
}
