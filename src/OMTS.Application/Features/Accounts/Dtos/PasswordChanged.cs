using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Verification.Dtos;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <summary>Result of ACS-04: fresh tokens for this device (every other session was ended) and MSG18.</summary>
public sealed record PasswordChanged(AuthTokens Tokens, string Message);
