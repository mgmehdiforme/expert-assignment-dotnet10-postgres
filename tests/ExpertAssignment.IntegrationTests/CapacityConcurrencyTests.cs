using ExpertAssignment.Application;
using ExpertAssignment.Domain;
using ExpertAssignment.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace ExpertAssignment.IntegrationTests;

public sealed class CapacityConcurrencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;
    private ServiceProvider _secondInstanceProvider = null!;
    private int _specialtyId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _provider = BuildInstanceProvider();
        _secondInstanceProvider = BuildInstanceProvider();
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(); // same schema path as production
        var specialty = new Specialty { Name = "Technical" };
        var expert = new Expert { Name = "Capacity-5", Capacity = 5, IsActive = true, ExpertSpecialties = [new ExpertSpecialty { Specialty = specialty }] };
        db.AddRange(specialty, expert);
        await db.SaveChangesAsync();
        _specialtyId = specialty.Id;
    }

    [Fact]
    public async Task Twenty_concurrent_requests_never_exceed_capacity()
    {
        var tasks = Enumerable.Range(1, 20).Select(async i =>
        {
            // Two independent DI containers model two separate ASP.NET Core instances.
            var instance = i % 2 == 0 ? _provider : _secondInstanceProvider;
            await using var scope = instance.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IRequestService>();
            return await service.CreateAsync(new CreateRequestCommand($"request-{i}", _specialtyId, RequestPriority.Normal), CancellationToken.None);
        });
        await Task.WhenAll(tasks);

        // Drain the durable queue just as a worker instance would.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IQueueProcessor>();
            for (var i = 0; i < 5; i++) await processor.ProcessBatchAsync(100, CancellationToken.None);
        }
        await using var verifyScope = _provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var assigned = await verify.Requests.CountAsync(x => x.Status == RequestStatus.Assigned);
        var pending = await verify.Requests.CountAsync(x => x.Status == RequestStatus.Pending);
        var openForExpert = await verify.Requests.CountAsync(x => x.AssignedExpertId != null && x.AssignedExpertId == 1 && x.Status == RequestStatus.Assigned);
        Assert.Equal(5, assigned);
        Assert.Equal(15, pending);
        Assert.Equal(5, openForExpert);
    }

    [Fact]
    public async Task Same_request_key_creates_only_one_request_across_instances()
    {
        var tasks = Enumerable.Range(0, 12).Select(async i =>
        {
            var instance = i % 2 == 0 ? _provider : _secondInstanceProvider;
            await using var scope = instance.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IRequestService>();
            return await service.CreateAsync(new CreateRequestCommand("same-idempotency-key", _specialtyId, RequestPriority.Normal), CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);
        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Requests.CountAsync(x => x.RequestKey == "same-idempotency-key"));
        Assert.Single(results.Select(x => x.Id).Distinct());
    }

    [Fact]
    public async Task Request_without_matching_expert_does_not_block_later_eligible_request()
    {
        await using (var setupScope = _provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unsupported = new Specialty { Name = "NoExpertSpecialty" };
            db.Specialties.Add(unsupported);
            await db.SaveChangesAsync();
            db.Requests.AddRange(
                new ServiceRequest { RequestKey = "blocked-no-expert", SpecialtyId = unsupported.Id, Priority = RequestPriority.Urgent, Status = RequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) },
                new ServiceRequest { RequestKey = "later-eligible", SpecialtyId = _specialtyId, Priority = RequestPriority.Normal, Status = RequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var workerScope = _provider.CreateAsyncScope())
        {
            var processor = workerScope.ServiceProvider.GetRequiredService<IQueueProcessor>();
            Assert.Equal(1, await processor.ProcessBatchAsync(100, CancellationToken.None));
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(RequestStatus.Pending, (await verify.Requests.SingleAsync(x => x.RequestKey == "blocked-no-expert")).Status);
        Assert.Equal(RequestStatus.Assigned, (await verify.Requests.SingleAsync(x => x.RequestKey == "later-eligible")).Status);
    }

    [Fact]
    public async Task Closing_request_releases_capacity_for_next_pending_request()
    {
        var created = new List<CreateRequestResult>();
        for (var i = 0; i < 6; i++)
        {
            await using var scope = _provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IRequestService>();
            created.Add(await service.CreateAsync(new CreateRequestCommand($"close-flow-{i}", _specialtyId, RequestPriority.Normal), CancellationToken.None));
        }
        Assert.Equal(RequestStatus.Pending, created[5].Status);

        // Close hands the freed slot to the queued request itself; no worker run is needed.
        await CloseAsync(created[0].Id);

        Assert.Equal(RequestStatus.Closed, await StatusAsync(created[0].Id));
        Assert.Equal(RequestStatus.Assigned, await StatusAsync(created[5].Id));
        await using var verifyScope = _provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(5, await verify.Requests.CountAsync(x => x.Status == RequestStatus.Assigned));
    }

    [Fact]
    public async Task Picks_expert_with_lowest_open_to_capacity_ratio()
    {
        // Task example: expert X has 2 of 4 open (0.5), expert Y has 3 of 10 open (0.3) -> Y.
        int specialtyId, xId, yId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var specialty = new Specialty { Name = "RatioCheck" };
            var x = new Expert { Name = "X", Capacity = 4, ExpertSpecialties = [new ExpertSpecialty { Specialty = specialty }] };
            var y = new Expert { Name = "Y", Capacity = 10, ExpertSpecialties = [new ExpertSpecialty { Specialty = specialty }] };
            db.AddRange(specialty, x, y);
            await db.SaveChangesAsync();
            (specialtyId, xId, yId) = (specialty.Id, x.Id, y.Id);
            for (var i = 0; i < 2; i++) db.Requests.Add(AssignedRequest($"ratio-x-{i}", specialtyId, xId));
            for (var i = 0; i < 3; i++) db.Requests.Add(AssignedRequest($"ratio-y-{i}", specialtyId, yId));
            await db.SaveChangesAsync();
        }

        var result = await CreateAsync("ratio-new", RequestPriority.Normal, specialtyId);

        Assert.Equal(RequestStatus.Assigned, result.Status);
        Assert.Equal(yId, result.AssignedExpertId);
    }

    [Fact]
    public async Task Close_serves_urgent_first_and_new_requests_wait_behind_the_queue()
    {
        for (var i = 0; i < 5; i++) await CreateAsync($"fill-{i}", RequestPriority.Normal);
        var normal = await CreateAsync("queued-normal", RequestPriority.Normal);
        var urgent = await CreateAsync("queued-urgent", RequestPriority.Urgent);
        Assert.Equal(RequestStatus.Pending, normal.Status);
        Assert.Equal(RequestStatus.Pending, urgent.Status);

        // First freed slot goes to the Urgent request even though the Normal one is older.
        await CloseAsync((await IdOfAsync("fill-0")));
        Assert.Equal(RequestStatus.Assigned, await StatusAsync(urgent.Id));
        Assert.Equal(RequestStatus.Pending, await StatusAsync(normal.Id));

        // A request arriving now must not take the next slot ahead of the queued Normal one.
        var latecomer = await CreateAsync("latecomer", RequestPriority.Normal);
        Assert.Equal(RequestStatus.Pending, latecomer.Status);
        await CloseAsync((await IdOfAsync("fill-1")));
        Assert.Equal(RequestStatus.Assigned, await StatusAsync(normal.Id));
        Assert.Equal(RequestStatus.Pending, await StatusAsync(latecomer.Id));
    }

    [Fact]
    public async Task New_request_does_not_jump_a_queued_request_when_capacity_is_free()
    {
        // A queued request the worker hasn't reached yet, while the expert has free capacity.
        long queuedId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var queued = new ServiceRequest { RequestKey = "waiting-for-worker", SpecialtyId = _specialtyId, Priority = RequestPriority.Normal, Status = RequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            db.Requests.Add(queued);
            await db.SaveChangesAsync();
            queuedId = queued.Id;
        }

        var fresh = await CreateAsync("fresh", RequestPriority.Normal);
        Assert.Equal(RequestStatus.Pending, fresh.Status);

        await using (var workerScope = _provider.CreateAsyncScope())
            await workerScope.ServiceProvider.GetRequiredService<IQueueProcessor>().ProcessBatchAsync(100, CancellationToken.None);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queuedRow = await verify.Requests.SingleAsync(x => x.Id == queuedId);
        var freshRow = await verify.Requests.SingleAsync(x => x.Id == fresh.Id);
        Assert.Equal(RequestStatus.Assigned, queuedRow.Status);
        Assert.Equal(RequestStatus.Assigned, freshRow.Status);
        Assert.True(queuedRow.AssignedAt <= freshRow.AssignedAt, "the queued request must be served first");
    }

    [Fact]
    public async Task Normal_request_waiting_30_minutes_is_served_before_a_fresh_urgent_one()
    {
        for (var i = 0; i < 5; i++) await CreateAsync($"busy-{i}", RequestPriority.Normal);
        long agedId, urgentId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var aged = new ServiceRequest { RequestKey = "aged-normal", SpecialtyId = _specialtyId, Priority = RequestPriority.Normal, Status = RequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-45) };
            var urgent = new ServiceRequest { RequestKey = "fresh-urgent", SpecialtyId = _specialtyId, Priority = RequestPriority.Urgent, Status = RequestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow };
            db.Requests.AddRange(aged, urgent);
            await db.SaveChangesAsync();
            (agedId, urgentId) = (aged.Id, urgent.Id);
        }

        await CloseAsync(await IdOfAsync("busy-0"));

        Assert.Equal(RequestStatus.Assigned, await StatusAsync(agedId));
        Assert.Equal(RequestStatus.Pending, await StatusAsync(urgentId));
    }

    private async Task<CreateRequestResult> CreateAsync(string key, RequestPriority priority, int? specialtyId = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRequestService>()
            .CreateAsync(new CreateRequestCommand(key, specialtyId ?? _specialtyId, priority), CancellationToken.None);
    }

    private async Task CloseAsync(long id)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IRequestService>().CloseAsync(id, CancellationToken.None);
    }

    private async Task<RequestStatus> StatusAsync(long id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Requests.AsNoTracking().SingleAsync(x => x.Id == id)).Status;
    }

    private async Task<long> IdOfAsync(string key)
    {
        await using var scope = _provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Requests.AsNoTracking().SingleAsync(x => x.RequestKey == key)).Id;
    }

    private static ServiceRequest AssignedRequest(string key, int specialtyId, int expertId) => new()
    {
        RequestKey = key, SpecialtyId = specialtyId, Priority = RequestPriority.Normal, Status = RequestStatus.Assigned,
        AssignedExpertId = expertId, CreatedAt = DateTimeOffset.UtcNow, AssignedAt = DateTimeOffset.UtcNow,
    };

    private ServiceProvider BuildInstanceProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AssignmentEngine>();
        services.AddScoped<IRequestService, RequestService>();
        services.AddScoped<IQueueProcessor, QueueProcessor>();
        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (_secondInstanceProvider is not null) await _secondInstanceProvider.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
