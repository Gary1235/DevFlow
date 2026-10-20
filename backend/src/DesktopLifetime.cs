using Microsoft.Extensions.Hosting;

namespace DevFlow.Desktop;

// Enabled by desktop or development launchers. EOF shuts services down if the parent dies.
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
