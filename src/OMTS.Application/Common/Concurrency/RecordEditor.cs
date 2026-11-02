
namespace OMTS.Application.Common.Concurrency;

/// <summary>Someone with the edit form of a record open since <see cref="StartedAt"/>.</summary>
public sealed record RecordEditor(Guid AccountId, string Name, DateTimeOffset StartedAt);
