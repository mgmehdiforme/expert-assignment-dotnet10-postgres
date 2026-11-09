using ExpertAssignment.Api;
using ExpertAssignment.Application;
using ExpertAssignment.Domain;
using ExpertAssignment.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var app = builder.Build();

// For a multi-instance deployment, migrations should ideally run once as a deployment job.
// This sample applies them on startup; use a single migration runner in production.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue<bool>("SeedDemoData")) await DemoSeeder.SeedAsync(db);
}

app.UseSwagger(); app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/specialties", async (CreateSpecialtyCommand command, AppDbContext db, CancellationToken ct) =>
{
    var name = command.Name.Trim();
    if (name.Length is 0 or > 100) return (IResult)Results.BadRequest(new { error = "Name must be 1-100 characters." });
    var specialty = new Specialty { Name = name }; db.Specialties.Add(specialty); await db.SaveChangesAsync(ct);
    return (IResult)Results.Created($"/api/specialties/{specialty.Id}", new { specialty.Id, specialty.Name });
});
app.MapPost("/api/experts", async (CreateExpertCommand command, AppDbContext db, CancellationToken ct) =>
{
    if (command.Capacity <= 0 || string.IsNullOrWhiteSpace(command.Name) || command.SpecialtyIds.Count == 0) return (IResult)Results.BadRequest(new { error = "Name, positive capacity and at least one specialty are required." });
    var specialties = await db.Specialties.Where(x => command.SpecialtyIds.Contains(x.Id)).ToListAsync(ct);
    if (specialties.Count != command.SpecialtyIds.Distinct().Count()) return (IResult)Results.BadRequest(new { error = "One or more specialties do not exist." });
    var expert = new Expert { Name = command.Name.Trim(), Capacity = command.Capacity, IsActive = true, ExpertSpecialties = specialties.Select(s => new ExpertSpecialty { Specialty = s }).ToList() };
    db.Experts.Add(expert); await db.SaveChangesAsync(ct);
    return (IResult)Results.Created($"/api/experts/{expert.Id}", new { expert.Id, expert.Name, expert.Capacity });
});
app.MapPost("/api/requests", async (CreateRequestCommand command, IRequestService service, CancellationToken ct) =>
{
    try
    {
        var result = await service.CreateAsync(command, ct);
        return result.AlreadyExisted ? (IResult)Results.Ok(result) : Results.Accepted($"/api/requests/{result.Id}", result);
    }
    catch (IdempotencyConflictException ex) { return (IResult)Results.Conflict(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { return (IResult)Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return (IResult)Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/requests/{id:long}", async (long id, IRequestService service, CancellationToken ct) =>
    await service.GetAsync(id, ct) is { } dto ? (IResult)Results.Ok(dto) : Results.NotFound());
app.MapPost("/api/requests/{id:long}/close", async (long id, IRequestService service, CancellationToken ct) =>
{
    try { await service.CloseAsync(id, ct); return (IResult)Results.NoContent(); }
    catch (KeyNotFoundException ex) { return (IResult)Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return (IResult)Results.Conflict(new { error = ex.Message }); }
});
app.Run();

public partial class Program { }
