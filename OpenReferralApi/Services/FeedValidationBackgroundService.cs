using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Services;
using OpenReferralApi.Core.Logging;
using OpenReferralApi.Logging;

namespace OpenReferralApi.Services;

/// <summary>
/// Background service that validates registered feeds every 24 hours at midnight
/// </summary>
internal sealed class FeedValidationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FeedValidationBackgroundService> _logger;
    private readonly TimeSpan _validationInterval;
    private readonly bool _runAtMidnight;
    private readonly bool _enabled;

    public FeedValidationBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<FeedValidationOptions> options,
        ILogger<FeedValidationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Read configuration from options
        _enabled = options.Value.Enabled;
        _validationInterval = TimeSpan.FromHours(options.Value.IntervalHours);
        _runAtMidnight = options.Value.RunAtMidnight;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.ServiceDisabled();
            return;
        }

        _logger.ServiceStarted(_validationInterval.TotalHours, _runAtMidnight);

        // Wait until first scheduled run
        await WaitForNextScheduledRunAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.ScheduledValidationStarted(DateTime.UtcNow);
                await ValidateAllFeedsAsync(stoppingToken).ConfigureAwait(false);
                _logger.ScheduledValidationCompleted(DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                _logger.ServiceStopping();
                throw;
            }
            // Remove catch-all Exception handler to comply with analyzer
            // If you want to log unexpected exceptions, consider rethrowing after logging

            // Wait for next scheduled run
            await WaitForNextScheduledRunAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForNextScheduledRunAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;

        if (_runAtMidnight)
        {
            // Calculate time until next midnight UTC
            var now = DateTime.UtcNow;
            var nextMidnight = now.Date.AddDays(1);
            delay = nextMidnight - now;

            _logger.NextValidationScheduledForMidnight(nextMidnight, delay.TotalHours);
        }
        else
        {
            // Use fixed interval
            delay = _validationInterval;
            _logger.NextValidationScheduled(delay.TotalHours);
        }

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            _logger.ServiceStopping();
        }
    }

    private async Task ValidateAllFeedsAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var scope = _serviceProvider.CreateScope();
        var feedValidationService = scope.ServiceProvider.GetRequiredService<IFeedValidationService>();

        try
        {
            // Get all registered feeds
            var feeds = await feedValidationService.GetAllFeedsAsync(cancellationToken).ConfigureAwait(false);

            _logger.FoundFeedsToValidate(feeds.Count);

            if (feeds.Count == 0)
            {
                _logger.NoFeedsFound();
                return;
            }
            var results = await feedValidationService.ValidateAndUpdateFeedsAsync(feeds, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Log summary
            var successCount = results.Count(r => r.IsUp);
            var validCount = results.Count(r => r.IsValid);
            var failedCount = results.Count(r => !r.IsUp);

            stopwatch.Stop();

            _logger.FeedValidationSummary(feeds.Count, successCount, validCount, failedCount, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _logger.ServiceStopping();
            throw;
        }
        // Remove catch-all Exception handler to comply with analyzer
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.BackgroundServiceStopping();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
