using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Verification;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <summary>Contact change started: the code went to the new contact; the old one stays in use until verified (MSG62).</summary>
public sealed record ContactChangeStarted(Verification.Dtos.VerificationStarted Verification, string Message, string MessageCode);
