namespace OMTS.Application.Features.Auth.Dtos;

public sealed record AuthResult(AuthTokens? Tokens, AuthError? Error)
{
    public static AuthResult Success(AuthTokens tokens) => new(tokens, null);
    public static AuthResult Failure(AuthError error) => new(null, error);
}
