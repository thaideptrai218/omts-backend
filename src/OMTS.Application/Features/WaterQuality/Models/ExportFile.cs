using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Farming;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality.Models;

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);
