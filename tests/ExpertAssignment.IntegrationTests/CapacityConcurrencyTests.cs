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

        await using (var closeScope = _provider.CreateAsyncScope())
        {
            var service = closeScope.ServiceProvider.GetRequiredService<IRequestService>();
            await service.CloseAsync(created[0].Id, CancellationToken.None);
        }
        await using (var workerScope = _provider.CreateAsyncScope())
        {
            var processor = workerScope.ServiceProvider.GetRequiredService<IQueueProcessor>();
            Assert.Equal(1, await processor.ProcessBatchAsync(100, CancellationToken.None));
        }
        await using var verifyScope = _provider.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(RequestStatus.Closed, (await verify.Requests.SingleAsync(x => x.Id == created[0].Id)).Status);
        Assert.Equal(RequestStatus.Assigned, (await verify.Requests.SingleAsync(x => x.Id == created[5].Id)).Status);
        Assert.Equal(5, await verify.Requests.CountAsync(x => x.Status == RequestStatus.Assigned));
    }

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
