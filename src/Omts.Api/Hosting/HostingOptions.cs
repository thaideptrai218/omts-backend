namespace Omts.Api.Hosting;

public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    public int RequestTimeoutSeconds { get; set; } = 30;
}
