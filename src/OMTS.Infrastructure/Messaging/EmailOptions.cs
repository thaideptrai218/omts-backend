
namespace OMTS.Infrastructure.Messaging;

/// <summary>"Email" section. Credentials come from user-secrets or environment variables only.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public const string SmtpProvider = "Smtp";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = SmtpProvider;
    public SmtpOptions Smtp { get; set; } = new();
    public FakeSenderOptions Fake { get; set; } = new();
}
