using ExpertAssignment.Domain;

namespace ExpertAssignment.Application;

public sealed record CreateRequestCommand(string RequestKey, int SpecialtyId, RequestPriority Priority);
public sealed record CreateRequestResult(long Id, RequestStatus Status, int? AssignedExpertId, bool AlreadyExisted);
public sealed record RequestDto(long Id, string RequestKey, int SpecialtyId, RequestPriority Priority, RequestStatus Status, int? AssignedExpertId, DateTimeOffset CreatedAt, DateTimeOffset? AssignedAt, DateTimeOffset? ClosedAt);
public sealed record CreateSpecialtyCommand(string Name);
public sealed record CreateExpertCommand(string Name, int Capacity, IReadOnlyCollection<int> SpecialtyIds);

public interface IRequestService
{
    Task<CreateRequestResult> CreateAsync(CreateRequestCommand command, CancellationToken ct);
    Task<RequestDto?> GetAsync(long id, CancellationToken ct);
    Task CloseAsync(long id, CancellationToken ct);
}
public interface IQueueProcessor { Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct); }
public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
