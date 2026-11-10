# Expert Assignment (PostgreSQL / .NET 10)

Clean Architecture sample for automatic expert assignment, durable pending queue, idempotency, priority aging, and multi-instance concurrency.

## Important concurrency design

Every assignment transaction:
1. Locks the pending request row using `FOR UPDATE SKIP LOCKED`.
2. Finds active experts matching the request specialty.
3. Locks all candidate expert rows in ascending ID order using PostgreSQL `FOR UPDATE`.
4. Re-reads assigned-request counts after acquiring the locks.
5. Selects the minimum `open / capacity` ratio (integer cross multiplication), then oldest `LastAssignedAt` (NULL first), then lowest ID.
6. Assigns and writes history in the same transaction.

This is safe across multiple API/worker instances because PostgreSQL row locks are database-wide. It also handles an expert shared by multiple specialties: both assignment paths lock that same expert row. All future code paths that change expert capacity/active state must lock the expert row too.

## Run with two API instances

```bash
docker compose up --build
```

- Swagger/API instance 1: http://localhost:8091/swagger
- Swagger/API instance 2: http://localhost:8092/swagger
- Both use the same PostgreSQL database (`127.0.0.1:5432`, database `expert_assignment`, user/password `postgres`/`postgres` — local dev only).
- All three containers use `restart: unless-stopped`, so they come back after a reboot.
- The first instance seeds Legal, Technical, Financial and sample experts.

For a production deployment, run EF migrations once as a deployment job rather than letting every replica migrate concurrently. The sample startup migration is convenient for local development.

## API examples

Create a specialty:
```http
POST /api/specialties
Content-Type: application/json
{"name":"Legal"}
```

Create an expert:
```http
POST /api/experts
Content-Type: application/json
{"name":"Expert A","capacity":5,"specialtyIds":[1]}
```

Create request (same RequestKey returns existing record; different payload with same key returns 409):
```http
POST /api/requests
Content-Type: application/json
{"requestKey":"client-operation-123","specialtyId":1,"priority":1}
```

Priority values: `0 = Normal`, `1 = Urgent`.

Close:
```http
POST /api/requests/1/close
```

Get status:
```http
GET /api/requests/1
```

## Solution

Open `ExpertAssignment.sln` (classic solution format) in Visual Studio / Rider / VS Code, or build from the command line:

```bash
dotnet build ExpertAssignment.sln
```

## Migrations

The schema is the EF Core migration `InitialCreate` in `src/ExpertAssignment.Infrastructure/Migrations` (with its model snapshot). `database/001_initial_schema.sql` is generated from it for review:

```bash
dotnet ef migrations script --idempotent --project src/ExpertAssignment.Infrastructure --startup-project src/ExpertAssignment.Api -o database/001_initial_schema.sql
```

After a model change, add a new migration (never edit `InitialCreate`):

```bash
dotnet ef migrations add <Name> --project src/ExpertAssignment.Infrastructure --startup-project src/ExpertAssignment.Api
```

The API calls `Database.Migrate()` on startup. For production, apply migrations once from a migration job. Do not have every replica run migrations simultaneously.

## Tests

```bash
dotnet test ExpertAssignment.sln
```

Requires Docker (Testcontainers starts a throwaway PostgreSQL 17 container per test and applies the real EF migration). The tests cover:
- 20 concurrent requests from two service instances against one expert with capacity 5 → 5 Assigned, 15 Pending.
- The same `RequestKey` sent 12 times concurrently → one request.
- A request with no matching expert does not block a later eligible one.
- Closing a request hands the freed slot straight to the next queued request.
- The task's ratio example: 2/4 vs 3/10 → the 3/10 expert is chosen.
- On close, Urgent beats an older Normal, and a request arriving afterwards waits behind the queue.
- A new request does not jump a queued one even when capacity is free.
- A Normal request waiting 30+ minutes is served before a fresh Urgent one.

## Priority and starvation policy

Urgent requests are in the top tier. A Normal request waiting at least 30 minutes is promoted into the same tier. FIFO by `CreatedAt`, then ID, is used within each tier. Requests without an eligible expert are skipped; later eligible requests are still attempted. The order is defined once in `QueueOrder.cs` and used everywhere below.

How the queue is served, so new arrivals never jump it:
- **On close**: the same transaction that closes a request gives the freed slot to the first queued request that expert can serve, while it still holds the expert's row lock. A request created at that moment waits for the lock, then finds the expert full.
- **On create**: the immediate assignment is skipped when an older or higher-tier request of the same specialty is queued; the new request waits its turn.
- **Worker**: every instance polls the durable database queue (1.5 s) and catches everything else (e.g. capacity freed some other way). A restart loses no pending work.

Example (one expert, full; a slot frees at 10:25): queued N1 Normal 09:40 (aged → top tier), U1 Urgent 10:20, N2 Normal 10:10 → served N1, U1, N2. A request for a specialty with no available expert is skipped and does not block them.

## Production notes

- Configure secrets via environment variables/secret manager; do not use sample passwords.
- Add authentication/authorization and rate limiting before public exposure.
- Consider a partial index for pending requests after measuring workload.
- Add an outbox if assignment triggers external side effects (email, webhook, CRM). Do not send external calls inside the DB transaction.
- For expert capacity/status updates, acquire the corresponding expert row lock before changing those fields.
- `RequestKey` uniqueness is enforced by PostgreSQL, not only application code.
