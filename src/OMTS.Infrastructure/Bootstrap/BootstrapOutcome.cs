using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Audit;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence.Seed;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>Exit code and console lines of the bootstrap command; never contains a password.</summary>
public sealed record BootstrapOutcome(int ExitCode, IReadOnlyList<string> Messages);
