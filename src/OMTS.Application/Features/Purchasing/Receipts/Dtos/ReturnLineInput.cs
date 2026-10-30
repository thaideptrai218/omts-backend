using System.Globalization;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Common;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record ReturnLineInput(Guid? ReceiptLineId, decimal? Quantity);
