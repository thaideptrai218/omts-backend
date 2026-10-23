using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.LaborCost;

/// <summary>Validated pay rate (LAB-06 data rules).</summary>
public sealed record PayRateInput(
    SalaryType SalaryType, decimal Amount, int? StandardWorkingDays, IReadOnlyList<PayComponent> Components, DateOnly ValidFrom, DateOnly? ValidTo)
{
    /// <summary>MSG01 for missing/invalid fields (first), then MSG41 for amounts that are not multiples of 1,000.</summary>
    public static Result<PayRateInput> From(PayRateBody? body, bool forWorker)
    {
        var invalid = new List<string>();
        var notThousands = new List<string>();
        if (body?.SalaryType is not { } salaryType || (forWorker && salaryType != SalaryType.DailyWage))
        {
            invalid.Add("Salary type");
        }

        CheckAmount(body?.Amount, "Amount", invalid, notThousands);
        int? standardDays = null;
        if (body?.SalaryType == SalaryType.PerWorkingDay)
        {
            if (body.StandardWorkingDays is not (>= 1 and <= 31))
            {
                invalid.Add("Standard working days");
            }

            standardDays = body.StandardWorkingDays;
        }

        var components = new List<PayComponent>();
        foreach (var component in body?.Components ?? [])
        {
            var name = LaborInputRules.Required(component.Name, 100, "Component name", invalid);
            CheckAmount(component.Amount, "Component amount", invalid, notThousands);
            if (component.Kind is null)
            {
                invalid.Add("Component kind");
            }

            if (component.Basis is null || (component.Kind == PayComponentKind.Deduction && component.Basis != PayComponentBasis.PerMonth))
            {
                invalid.Add("Component basis");
            }

            if (name is not null && component is { Kind: { } kind, Amount: { } amount, Basis: { } basis })
            {
                components.Add(new PayComponent { Kind = kind, Name = name, Amount = amount, Basis = basis });
            }
        }

        if (body?.ValidFrom is null)
        {
            invalid.Add("Valid from");
        }

        if (body?.ValidTo < body?.ValidFrom)
        {
            invalid.Add("Valid to");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        if (notThousands.Count > 0)
        {
            return new AppError(ErrorKind.Validation, UserMessages.NotThousands, new Dictionary<string, object?> { ["field"] = notThousands[0] });
        }

        return new PayRateInput(body!.SalaryType!.Value, body.Amount!.Value, standardDays, components, body.ValidFrom!.Value, body.ValidTo);
    }

    private static void CheckAmount(decimal? amount, string field, List<string> invalid, List<string> notThousands)
    {
        if (amount is not > 0)
        {
            invalid.Add(field);
        }
        else if (!VndAmounts.IsThousands(amount.Value))
        {
            notThousands.Add(field);
        }
    }
}
