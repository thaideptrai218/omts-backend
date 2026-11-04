using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The audit rows of a cultivation log (GBR-02): the record code is the code of its raft, as a log has none.</summary>
public static class CultivationLogAudit
{
    public const string Created = "CultivationLog.Created";
    public const string Corrected = "CultivationLog.Corrected";
    public const string Removed = "CultivationLog.Removed";
    public const string RecordType = nameof(CultivationLog);

    /// <summary>The log with its cycle and raft.</summary>
    internal static AuditEntry Entry(string action, CultivationLog log) => new(action, RecordType)
    {
        RecordId = log.Id, RecordCode = log.Cycle.Raft.Code, ZoneId = log.Cycle.Raft.ZoneId,
    };
}
