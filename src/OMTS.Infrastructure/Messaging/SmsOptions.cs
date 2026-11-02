
namespace OMTS.Infrastructure.Messaging;

/// <summary>"Sms" section. Only the fake exists until the real SMS provider ticket; "None" sends nothing.</summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";
    public const string FakeProvider = "Fake";
    public const string NoneProvider = "None";

    public string Provider { get; set; } = NoneProvider;
    public FakeSenderOptions Fake { get; set; } = new();
}
