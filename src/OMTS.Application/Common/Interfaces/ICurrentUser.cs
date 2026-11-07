namespace OMTS.Application.Common.Interfaces;

/// <summary>The caller of the current request, read from the validated access token.</summary>
public interface ICurrentUser
{
    Guid? AccountId { get; }
    Guid? SessionId { get; }
}
