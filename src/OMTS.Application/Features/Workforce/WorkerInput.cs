using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Workforce.Dtos;

namespace OMTS.Application.Features.Workforce;

/// <summary>Validated, normalised seasonal worker fields (LAB-02 data rules).</summary>
public sealed record WorkerInput(string FullName, string? PhoneNumber, string? CitizenId)
{
    public const string FullNameField = "Full name";
    public const string PhoneField = "Phone number";
    public const string CitizenIdField = "Citizen ID";
    public const string ContactField = "Phone number or citizen ID";

    /// <summary>The normalised input, or the names of every invalid field.</summary>
    public static (WorkerInput? Input, List<string> Invalid) From(SaveWorkerRequest request)
    {
        var invalid = new List<string>();
        var fullName = LaborInputRules.Required(request.FullName, 100, FullNameField, invalid);

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            phone = PhoneNumbers.Normalize(request.PhoneNumber);
            if (phone is null)
            {
                invalid.Add(PhoneField);
            }
        }

        string? citizenId = null;
        if (!string.IsNullOrWhiteSpace(request.CitizenId))
        {
            citizenId = request.CitizenId.Trim();
            if (!LaborInputRules.IsCitizenId(citizenId))
            {
                invalid.Add(CitizenIdField);
            }
        }

        if (string.IsNullOrWhiteSpace(request.PhoneNumber) && string.IsNullOrWhiteSpace(request.CitizenId))
        {
            invalid.Add(ContactField);
        }

        return invalid.Count > 0 ? (null, invalid) : (new WorkerInput(fullName!, phone, citizenId), invalid);
    }
}
