using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Services;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Services;

/// <summary>
/// Warms frequently-used remote schemas into cache after startup.
/// </summary>
internal sealed class SchemaWarmupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SchemaWarmupBackgroundService> _logger;
    private readonly SpecificationOptions _options;
    private readonly CacheOptions _cacheOptions;
    private readonly ISchemaWarmupStatusTracker _statusTracker;
    private readonly ISchemaWarmupExecutor _executor;

    public SchemaWarmupBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<SpecificationOptions> options,
        IOptions<CacheOptions> cacheOptions,
        ISchemaWarmupStatusTracker statusTracker,
        ILogger<SchemaWarmupBackgroundService> logger,
        ISchemaWarmupExecutor? executor = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value ?? new SpecificationOptions();
        _cacheOptions = cacheOptions.Value ?? new CacheOptions();
        _statusTracker = statusTracker;
        _executor = executor ?? new SchemaWarmupExecutor();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            _statusTracker.MarkCompleted(true);
            return;
        }
        await _executor.WarmupAsync(_options, _cacheOptions, _statusTracker, _serviceProvider, _logger, stoppingToken).ConfigureAwait(false);
    }
}
