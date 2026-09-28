using ChamXanh.Api.Modules.Media;

namespace ChamXanh.Api.Modules.Listings;

/// <summary>Job định kỳ: hết hạn tin và dọn ảnh (BR-MED-03). Sẽ chuyển sang Hangfire khi có nhiều job hơn.</summary>
public class ListingMaintenanceWorker(IServiceScopeFactory scopes, ILogger<ListingMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var listings = scope.ServiceProvider.GetRequiredService<ListingService>();
                var expired = await listings.ExpireDueAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Đã chuyển {Count} tin sang hết hạn", expired);
                var dead = await listings.DeadListingIdsAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<MediaService>().CleanupAsync(dead, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Lỗi job bảo trì tin đăng");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
