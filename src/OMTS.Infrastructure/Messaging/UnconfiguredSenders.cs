using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Messaging;

internal static class UnconfiguredSenders
{
    public const string NotConfigured = "NOT_CONFIGURED";
}
