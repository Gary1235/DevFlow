using Microsoft.Extensions.Hosting;

namespace DevFlow.Desktop;

// Only enabled by the desktop launcher. EOF also shuts down services if the parent dies.
public sealed class DesktopLifetime(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stoppingToken);
                if (line is null || line == "shutdown")
                {
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
