using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 应用生命周期托管服务：主窗口的显示交给 App.OnFrameworkInitializationCompleted，
    /// 这里只保留 Host 启动 / 停止的挂载点，便于后续扩展后台任务。
    /// </summary>
    public class ApplicationHostService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;

        public ApplicationHostService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
