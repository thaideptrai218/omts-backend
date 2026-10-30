using OMTS.Application.Common.Models.Messaging;
namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>
/// Push messages waiting for the current unit of work: they are sent only after
/// <see cref="Persistence.IUnitOfWork.SaveChangesAsync"/> commits, and dropped when it fails, so a push
/// never announces a change that was not saved.
/// </summary>
public interface IPushOutbox
{
    void Enqueue(PushMessage message);
}
