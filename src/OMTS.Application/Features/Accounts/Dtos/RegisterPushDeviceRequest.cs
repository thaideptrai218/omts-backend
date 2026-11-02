using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.Accounts.Dtos;

/// <param name="PermissionGranted">False when the user declined notifications on the device.</param>
public sealed record RegisterPushDeviceRequest(string Token, string Platform, bool PermissionGranted);
