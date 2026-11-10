using System.Data;
using ExpertAssignment.Application;
using ExpertAssignment.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ExpertAssignment.Infrastructure;

/// <summary>
/// All assignment writers lock candidate expert rows in ascending ID order.
/// PostgreSQL row locks are shared by every API/worker instance using this database.
/// </summary>
public sealed class AssignmentEngine(AppDbContext db, IClock clock)
{
    public async Task<bool> TryAssignAsync(long requestId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // SKIP LOCKED lets another worker process a different pending request.
        var lockedRequests = await db.Requests.FromSqlInterpolated($"SELECT * FROM requests WHERE \"Id\" = {requestId} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
        var request = lockedRequests.SingleOrDefault();
        if (request is null || request.Status != RequestStatus.Pending)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var candidateIds = await db.ExpertSpecialties.AsNoTracking()
            .Where(x => x.SpecialtyId == request.SpecialtyId && x.Expert.IsActive && x.Expert.Capacity > 0)
            .Select(x => x.ExpertId).Distinct().OrderBy(x => x).ToArrayAsync(ct);

        if (candidateIds.Length == 0)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        // Lock every eligible expert in a stable order. A shared expert across two
        // specialties is therefore serialized too; this protects global capacity.
        var idsParam = new NpgsqlParameter("ids", candidateIds)
        { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer };
        _ = await db.Experts.FromSqlRaw(
            "SELECT * FROM experts WHERE \"Id\" = ANY (@ids) ORDER BY \"Id\" FOR UPDATE", idsParam)
            .ToListAsync(ct);

        // Reload after obtaining locks; READ COMMITTED gives a fresh statement snapshot.
        var experts = await db.Experts.Include(x => x.ExpertSpecialties)
            .Where(x => candidateIds.Contains(x.Id) && x.IsActive && x.Capacity > 0 && x.ExpertSpecialties.Any(s => s.SpecialtyId == request.SpecialtyId))
            .OrderBy(x => x.Id).ToListAsync(ct);

        var openCounts = await db.Requests.AsNoTracking()
            .Where(x => x.Status == RequestStatus.Assigned && x.AssignedExpertId != null && candidateIds.Contains(x.AssignedExpertId.Value))
            .GroupBy(x => x.AssignedExpertId!.Value)
            .Select(g => new { ExpertId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ExpertId, x => x.Count, ct);

        var candidates = experts
            .Select(e => new Candidate(e, openCounts.GetValueOrDefault(e.Id)))
            .Where(x => x.Open < x.Expert.Capacity)
            .ToList();
        candidates.Sort(CandidateComparer.Instance);
        var selected = candidates.FirstOrDefault()?.Expert;

        if (selected is null)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        Assign(request, selected);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Gives a just-freed slot of <paramref name="expert"/> to the first queued request it can serve.
    /// Must run inside the caller's transaction while the caller holds the expert's row lock, so no
    /// newly created request can take the slot first. The queued request is locked with SKIP LOCKED
    /// (never waited on), and no other expert is locked, so this cannot deadlock with TryAssignAsync.
    /// Returns the assigned request id, or null when nothing is queued or the expert can't take work.
    /// </summary>
    public async Task<long?> AssignNextQueuedToLockedExpertAsync(Expert expert, CancellationToken ct)
    {
        if (!expert.IsActive) return null;
        var open = await db.Requests.CountAsync(x => x.Status == RequestStatus.Assigned && x.AssignedExpertId == expert.Id, ct);
        if (open >= expert.Capacity) return null;

        var next = (await db.Requests.FromSqlRaw($$"""
            SELECT r.*
            FROM requests r
            WHERE r."Status" = 0
              AND r."SpecialtyId" IN (SELECT es."SpecialtyId" FROM expert_specialties es WHERE es."ExpertId" = @expertId)
            ORDER BY {{QueueOrder.OrderBy}}
            LIMIT 1
            FOR UPDATE OF r SKIP LOCKED
            """, new NpgsqlParameter("expertId", expert.Id)).ToListAsync(ct)).SingleOrDefault();
        if (next is null) return null;

        Assign(next, expert);
        await db.SaveChangesAsync(ct);
        return next.Id;
    }

    private void Assign(ServiceRequest request, Expert expert)
    {
        var now = clock.UtcNow;
        request.Status = RequestStatus.Assigned;
        request.AssignedExpertId = expert.Id;
        request.AssignedAt = now;
        expert.LastAssignedAt = now;
        db.AssignmentHistory.Add(new AssignmentHistory { RequestId = request.Id, ExpertId = expert.Id, AssignedAt = now });
    }

    private sealed record Candidate(Expert Expert, int Open);

    private sealed class CandidateComparer : IComparer<Candidate>
    {
        public static readonly CandidateComparer Instance = new();
        public int Compare(Candidate? x, Candidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            // Compare ratios without floating-point rounding: x.Open/x.Capacity vs y.Open/y.Capacity.
            var ratio = ((long)x.Open * y.Expert.Capacity).CompareTo((long)y.Open * x.Expert.Capacity);
            if (ratio != 0) return ratio;
            var lastAssigned = Nullable.Compare(x.Expert.LastAssignedAt, y.Expert.LastAssignedAt);
            if (lastAssigned != 0) return lastAssigned; // null (never assigned) wins
            return x.Expert.Id.CompareTo(y.Expert.Id);
        }
    }
}
