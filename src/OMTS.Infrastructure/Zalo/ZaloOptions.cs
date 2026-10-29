namespace OMTS.Infrastructure.Zalo;

/// <summary>
/// "Zalo" section: how the server checks the tokens of the Zalo Mini App. <c>Graph</c> calls the Zalo Open
/// API with the Zalo App secret; <c>Fake</c> (Development only) reads <c>fake:</c> tokens; unset answers
/// MSG04, so a deployment without a secret never links anyone.
/// </summary>
public sealed class ZaloOptions
{
    public const string SectionName = "Zalo";
    public const string GraphProvider = "Graph";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = "";

    /// <summary>Secret key of the Zalo App (developers.zalo.me, "Quản lý ứng dụng"); user-secrets or environment only.</summary>
    public string AppSecret { get; set; } = "";

    public string BaseUrl { get; set; } = "https://graph.zalo.me/";

    /// <summary>After this a Zalo call is abandoned and the user gets MSG04.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
