using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

/// <summary>All three fields or none (LAB-01).</summary>
public sealed record EmployeeBankAccountInput(string? BankName, string? AccountNumber, string? AccountHolderName);
