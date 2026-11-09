using ExpertAssignment.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertAssignment.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(config.GetConnectionString("Default"), x => x.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AssignmentEngine>();
        services.AddScoped<IRequestService, RequestService>();
        services.AddScoped<IQueueProcessor, QueueProcessor>();
        return services;
    }
}
