namespace OMTS.Application.Common;

/// <summary>
/// The acting account of work that runs outside an HTTP request, such as the Development demo seed
/// (<c>seed --bootstrap --demo</c>), so the application services see a logged-in Owner. One instance per DI
/// scope. The API's <c>ICurrentUser</c> reads it only when there is no HTTP request, so a request is never
/// affected: its caller always comes from the validated access token.
/// </summary>
public sealed class AmbientActor
{
    public Guid? AccountId { get; private set; }

    /// <summary>Acts as <paramref name="accountId"/> until the returned handle is disposed, then as before.</summary>
    public IDisposable Use(Guid accountId)
    {
        var previous = AccountId;
        AccountId = accountId;
        return new Reset(this, previous);
    }

    private sealed class Reset(AmbientActor actor, Guid? previous) : IDisposable
    {
        public void Dispose() => actor.AccountId = previous;
    }
}
