using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Employees.Dtos;

public sealed record EmployeeBankAccountView(string BankName, string AccountNumber, string AccountHolderName);
