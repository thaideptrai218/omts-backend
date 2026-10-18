namespace OMTS.Domain.Common;

/// <summary>
/// Entity whose status is a row of the Status catalog (BR-29). Values are in <see cref="Statuses.StatusIds"/>.
/// </summary>
public interface IHasStatus
{
    int StatusId { get; set; }
}
