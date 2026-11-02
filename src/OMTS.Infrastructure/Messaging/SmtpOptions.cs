
namespace OMTS.Infrastructure.Messaging;

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;

    /// <summary>
    /// <c>StartTls</c> (port 587, required), <c>SslOnConnect</c> (port 465) or <c>None</c> (plain local test
    /// servers; refused outside Development because credentials would travel in clear text).
    /// </summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "OMTS";
    public int TimeoutSeconds { get; set; } = 15;
}
