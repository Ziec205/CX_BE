using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChamXanh.Api.Common;

/// <summary>Việc chạy một lần khi khởi động: tạo index, seed dữ liệu. Mỗi module đăng ký của riêng mình.</summary>
public interface IStartupTask
{
    int Order => 0;
    Task RunAsync(IServiceProvider services, CancellationToken ct);
}

public class DelegateStartupTask(Func<IServiceProvider, CancellationToken, Task> run, int order = 0) : IStartupTask
{
    public int Order => order;
    public Task RunAsync(IServiceProvider services, CancellationToken ct) => run(services, ct);
}

/// <summary>Hoàn tất khi mọi startup task đã chạy xong. App mở cổng trước khi seed (xem Program.cs),
/// nên ai cần dữ liệu seed ngay (vd integration test) thì đợi Ready.</summary>
public class StartupGate
{
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => _ready.Task;
    internal void Complete() => _ready.TrySetResult();
    internal void Fail(Exception e) => _ready.TrySetException(e);
}

public static class StartupTaskExtensions
{
    public static IServiceCollection AddStartupTask(this IServiceCollection s, Func<IServiceProvider, CancellationToken, Task> run, int order = 0)
    {
        s.TryAddSingleton<StartupGate>();
        return s.AddSingleton<IStartupTask>(new DelegateStartupTask(run, order));
    }

    public static async Task RunStartupTasksAsync(this WebApplication app)
    {
        var gate = app.Services.GetRequiredService<StartupGate>();
        try
        {
            using var scope = app.Services.CreateScope();
            foreach (var task in scope.ServiceProvider.GetServices<IStartupTask>().OrderBy(t => t.Order))
                await task.RunAsync(scope.ServiceProvider, CancellationToken.None);
            gate.Complete();
        }
        catch (Exception e) { gate.Fail(e); throw; }
    }
}
