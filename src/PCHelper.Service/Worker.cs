using PCHelper.Ipc;
using PCHelper.Contracts;

namespace PCHelper.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    PCHelperRuntime runtime,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.InitializeAsync(stoppingToken).ConfigureAwait(false);
            ServiceLog.Initialised(logger, DateTimeOffset.UtcNow);

            NamedPipeRequestServer server = new(
                ProtocolConstants.ServicePipeName,
                (request, client, token) => runtime.HandleRequestAsync(request, client, token));
            Task serverTask = server.RunAsync(stoppingToken);
            Task pollingTask = PollAsync(stoppingToken);
            await Task.WhenAll(serverTask, pollingTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ServiceLog.ServiceFailed(logger, exception);
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        ServiceLog.Stopping(logger);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        int tick = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                bool persist = ++tick % 5 == 0;
                await runtime.RefreshAsync(persist, cancellationToken).ConfigureAwait(false);
                if (tick % 3600 == 0)
                {
                    await runtime.EnforceRetentionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ServiceLog.RefreshFailed(logger, exception);
            }
        }
    }
}
