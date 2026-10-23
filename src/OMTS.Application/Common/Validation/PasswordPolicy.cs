namespace OMTS.Application.Common.Validation;

/// <summary>Password rule of ACS (shared rules): at least 8 characters with at least one letter and one digit.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public static bool IsValid(string? password) =>
        password is { Length: >= MinimumLength and <= MaximumLength }
        && password.Any(char.IsLetter)
        && password.Any(char.IsDigit);
}
