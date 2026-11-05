namespace OMTS.Application.Features.Auth.Dtos;

/// <param name="PauseLeft">With <see cref="AuthError.TemporarilyLocked"/>: what is left of the pause, {retry_time} of MSG-ACCOUNT-06.</param>
public sealed record AuthResult(AuthTokens? Tokens, AuthError? Error, TimeSpan? PauseLeft = null)
{
    public static AuthResult Success(AuthTokens tokens) => new(tokens, null);
    public static AuthResult Failure(AuthError error) => new(null, error);
    public static AuthResult Paused(TimeSpan pauseLeft) => new(null, AuthError.TemporarilyLocked, pauseLeft);
}
