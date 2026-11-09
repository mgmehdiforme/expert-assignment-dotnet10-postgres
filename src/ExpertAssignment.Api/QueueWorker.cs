using ExpertAssignment.Application;

namespace ExpertAssignment.Api;

public sealed class QueueWorker(IServiceScopeFactory scopeFactory, ILogger<QueueWorker> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(config.GetValue("Queue:PollIntervalMs", 1500));
        var batchSize = config.GetValue("Queue:BatchSize", 100);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IQueueProcessor>();
                var assigned = await processor.ProcessBatchAsync(batchSize, stoppingToken);
                if (assigned == 0) await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue processing failed; retrying after delay.");
                await Task.Delay(interval, stoppingToken);
            }
        }
    }
}
