using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IPortWeighingRepository
{
    void Add(PortWeighing weighing);

    /// <summary>A cage added to a weighing already saved.</summary>
    void AddCage(PortWeighingCage cage);

    /// <summary>The cages a correction no longer has: only a weighing the trader has not confirmed is corrected.</summary>
    void RemoveCages(IReadOnlyCollection<PortWeighingCage> cages);

    /// <summary>The weighing with its request (customer), cages (lot and raft) and Completed invoice, tracked.</summary>
    Task<PortWeighing?> GetForEditAsync(Guid weighingId, CancellationToken cancellationToken);

    /// <summary>The same as <see cref="GetForEditAsync"/>, read only.</summary>
    Task<PortWeighing?> GetAsync(Guid weighingId, CancellationToken cancellationToken);

    /// <summary>The weighings of a request, newest first, read only.</summary>
    Task<IReadOnlyList<PortWeighing>> ListOfRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>One page of the weighings of a customer profile, newest first, read only, and how many it has in all.</summary>
    Task<(IReadOnlyList<PortWeighing> Items, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The request has a weighing, confirmed or not: its harvest can no longer be marked not done (decision of 11/11/2026).</summary>
    Task<bool> AnyOfRequestAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>How many cages of each lot the weighings of the request hold, leaving out <paramref name="exceptWeighingId"/>: a lot is not weighed past its cages.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CagesByLotOfRequestAsync(Guid requestId, Guid? exceptWeighingId, CancellationToken cancellationToken);

    /// <summary>A weighing of the request still waits for the trader.</summary>
    Task<bool> HasUnconfirmedAsync(Guid requestId, CancellationToken cancellationToken);
}
