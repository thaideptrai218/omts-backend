using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items.Dtos;

public sealed record SaveFarmingInputItemRequest(
    string? Code, string? Name, InputItemGroup? Group, string? Unit, string? Description, string? RowVersion = null);
