using ExpertAssignment.Application;
using ExpertAssignment.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExpertAssignment.Infrastructure;

public sealed class RequestService(AppDbContext db, AssignmentEngine engine, IClock clock) : IRequestService
{
    public async Task<CreateRequestResult> CreateAsync(CreateRequestCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.RequestKey) || command.RequestKey.Length > 200)
            throw new ArgumentException("RequestKey is required and must be at most 200 characters.");
        if (!Enum.IsDefined(command.Priority)) throw new ArgumentException("Invalid priority.");
        if (!await db.Specialties.AnyAsync(x => x.Id == command.SpecialtyId, ct))
            throw new KeyNotFoundException($"Specialty {command.SpecialtyId} was not found.");

        var entity = new ServiceRequest { RequestKey = command.RequestKey.Trim(), SpecialtyId = command.SpecialtyId, Priority = command.Priority, Status = RequestStatus.Pending, CreatedAt = clock.UtcNow };
        db.Requests.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(entity).State = EntityState.Detached;
            var existing = await db.Requests.AsNoTracking().SingleAsync(x => x.RequestKey == command.RequestKey.Trim(), ct);
            if (existing.SpecialtyId != command.SpecialtyId || existing.Priority != command.Priority)
                throw new IdempotencyConflictException("This RequestKey was already used with a different payload.");
            return ToResult(existing, true);
        }

        // Fast path: assign immediately, unless an older or higher-priority request of the same
        // specialty is still queued. Then the new request waits its turn and the worker (or the
        // next close) serves the queue in order. Anything not assigned here is retried by the worker.
        if (!await HasQueuedRequestAheadAsync(entity.Id, ct))
            await engine.TryAssignAsync(entity.Id, ct);
        await db.Entry(entity).ReloadAsync(ct);
        return ToResult(entity, false);
    }

    private async Task<bool> HasQueuedRequestAheadAsync(long requestId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<bool>($$"""
            SELECT EXISTS (
                SELECT 1
                FROM requests me
                JOIN requests r ON r."SpecialtyId" = me."SpecialtyId" AND r."Id" <> me."Id"
                WHERE me."Id" = {0}
                  AND r."Status" = 0
                  AND {{QueueOrder.KeyOf("r")}} < {{QueueOrder.KeyOf("me")}}
            ) AS "Value"
            """, requestId).SingleAsync(ct);

    public async Task<RequestDto?> GetAsync(long id, CancellationToken ct)
    {
        var x = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return x is null ? null : new RequestDto(x.Id, x.RequestKey, x.SpecialtyId, x.Priority, x.Status, x.AssignedExpertId, x.CreatedAt, x.AssignedAt, x.ClosedAt);
    }

    public async Task CloseAsync(long id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockedRequests = await db.Requests.FromSqlInterpolated($"SELECT * FROM requests WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(ct);
        var request = lockedRequests.SingleOrDefault() ?? throw new KeyNotFoundException($"Request {id} was not found.");
        if (request.Status == RequestStatus.Closed) { await tx.CommitAsync(ct); return; }
        if (request.Status != RequestStatus.Assigned || request.AssignedExpertId is null)
            throw new InvalidOperationException("Only an assigned request can be closed.");

        // Same lock order as assignment: request first, then expert.
        var expert = (await db.Experts.FromSqlInterpolated($"SELECT * FROM experts WHERE \"Id\" = {request.AssignedExpertId.Value} FOR UPDATE")
            .ToListAsync(ct)).Single();
        var history = await db.AssignmentHistory.Where(x => x.RequestId == id && x.UnassignedAt == null)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (history is not null) history.UnassignedAt = clock.UtcNow;
        request.Status = RequestStatus.Closed;
        request.ClosedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        // Hand the freed slot to the next queued request in the same transaction, while the
        // expert lock is still held. Otherwise a request created in the gap before the worker's
        // next poll could take the slot ahead of older or Urgent queued requests.
        await engine.AssignNextQueuedToLockedExpertAsync(expert, ct);
        await tx.CommitAsync(ct);
    }

    private static CreateRequestResult ToResult(ServiceRequest x, bool existed) => new(x.Id, x.Status, x.AssignedExpertId, existed);
}

public sealed class IdempotencyConflictException(string message) : Exception(message);
