namespace OMTS.Application.Common.Models;

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>Correct, but hashed with outdated parameters; a new hash should be stored.</summary>
    SuccessRehashNeeded,
}
