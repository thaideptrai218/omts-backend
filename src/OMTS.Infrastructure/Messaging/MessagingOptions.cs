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

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "OMTS";
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>"Sms" section. Only the fake exists until the real SMS provider ticket.</summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = FakeProvider;
    public FakeSenderOptions Fake { get; set; } = new();
}

/// <summary>"Push" section. Only the fake exists until the real push provider ticket.</summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = FakeProvider;
    public FakeSenderOptions Fake { get; set; } = new();
}

public sealed class FakeSenderOptions
{
    /// <summary>Recipients (phone numbers, emails or account ids) that get a failed result, for tests.</summary>
    public List<string> FailFor { get; set; } = [];
}
