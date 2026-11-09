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

- Swagger/API instance 1: http://localhost:8081/swagger
- Swagger/API instance 2: http://localhost:8082/swagger
- Both use the same PostgreSQL database.
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

## Migrations

The initial schema is in `database/001_initial_schema.sql`. To create EF migration metadata in a local SDK environment:

```bash
dotnet tool install --global dotnet-ef --version 10.0.0
dotnet ef migrations add InitialCreate --project src/ExpertAssignment.Infrastructure --startup-project src/ExpertAssignment.Api
```

The API calls `Database.Migrate()` on startup. For production, apply migrations once from a migration job. Do not have every replica run migrations simultaneously.

## Tests

```bash
dotnet test tests/ExpertAssignment.IntegrationTests/ExpertAssignment.IntegrationTests.csproj
```

The Testcontainers test starts a real PostgreSQL 17 container and submits 20 concurrent requests against one expert with capacity 5. Expected: 5 Assigned, 15 Pending, never more than 5 open.

## Priority and starvation policy

Urgent requests are in the top tier. A Normal request waiting at least 30 minutes is promoted into the same tier. FIFO by `CreatedAt`, then ID, is used within each tier. Requests without an eligible expert are skipped; later eligible requests are still attempted. The worker polls the durable database queue, so a restart does not lose pending work.

## Production notes

- Configure secrets via environment variables/secret manager; do not use sample passwords.
- Add authentication/authorization and rate limiting before public exposure.
- Consider a partial index for pending requests after measuring workload.
- Add an outbox if assignment triggers external side effects (email, webhook, CRM). Do not send external calls inside the DB transaction.
- For expert capacity/status updates, acquire the corresponding expert row lock before changing those fields.
- `RequestKey` uniqueness is enforced by PostgreSQL, not only application code.

## EF migration commands

The repository includes the `InitialCreate` EF Core migration under `src/ExpertAssignment.Infrastructure/Migrations` and the equivalent reviewable SQL under `database/001_initial_schema.sql`.

```bash
dotnet tool install --global dotnet-ef --version 10.0.0
dotnet ef database update --project src/ExpertAssignment.Infrastructure --startup-project src/ExpertAssignment.Api
```

Do not run `migrations add InitialCreate` again; that migration is already included.
