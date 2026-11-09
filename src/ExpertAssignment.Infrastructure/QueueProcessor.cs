using ExpertAssignment.Application;
using Microsoft.EntityFrameworkCore;

namespace ExpertAssignment.Infrastructure;

public sealed class QueueProcessor(AppDbContext db, AssignmentEngine engine) : IQueueProcessor
{
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        // Filter out pending requests that have no active matching expert with free
        // capacity. This prevents an unserviceable request from blocking later work.
        // Capacity is checked again under expert row locks inside TryAssignAsync.
        var ids = await db.Database.SqlQueryRaw<long>("""
            SELECT r."Id" AS "Value"
            FROM requests r
            WHERE r."Status" = 0
              AND EXISTS (
                  SELECT 1
                  FROM expert_specialties es
                  JOIN experts e ON e."Id" = es."ExpertId"
                  WHERE es."SpecialtyId" = r."SpecialtyId"
                    AND e."IsActive" = TRUE
                    AND e."Capacity" > (
                        SELECT COUNT(*)
                        FROM requests open_request
                        WHERE open_request."Status" = 1
                          AND open_request."AssignedExpertId" = e."Id"
                    )
              )
            ORDER BY
              CASE WHEN r."Priority" = 1 OR r."CreatedAt" <= now() - interval '30 minutes' THEN 0 ELSE 1 END,
              r."CreatedAt",
              r."Id"
            LIMIT {0}
            """, batchSize).ToListAsync(ct);

        var assigned = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (await engine.TryAssignAsync(id, ct)) assigned++;
        }
        return assigned;
    }
}
