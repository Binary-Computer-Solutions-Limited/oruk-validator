using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Services;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Services;

/// <summary>
/// Warms frequently-used remote schemas into cache after startup.
/// </summary>
public class SchemaWarmupBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SchemaWarmupBackgroundService> _logger;
    private readonly SpecificationOptions _options;
    private readonly CacheOptions _cacheOptions;
    private readonly ISchemaWarmupStatusTracker _statusTracker;

    public SchemaWarmupBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<SpecificationOptions> options,
        IOptions<CacheOptions> cacheOptions,
        ISchemaWarmupStatusTracker statusTracker,
        ILogger<SchemaWarmupBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value ?? new SpecificationOptions();
        _cacheOptions = cacheOptions.Value ?? new CacheOptions();
        _statusTracker = statusTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WarmupEnabled)
        {
            _statusTracker.MarkSkipped("disabled");
            _logger.WarmupDisabled();
            return;
        }

        if (!_cacheOptions.Enabled)
        {
            _statusTracker.MarkSkipped("cache-disabled");
            _logger.WarmupSkippedCacheDisabled();
            return;
        }

        var urls = (_options.Urls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .Values
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            _statusTracker.MarkSkipped("no-urls");
            _logger.WarmupNoUrlsConfigured();
            return;
        }

        _statusTracker.MarkStarted(urls.Count);

        var delaySeconds = Math.Max(0, _options.WarmupStartupDelaySeconds);
        if (delaySeconds > 0)
        {
            _logger.WarmupStartingIn(delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
        }

        using var scope = _serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISchemaResolverService>();

        _logger.WarmupStartingForUrls(urls.Count);

        foreach (var url in urls)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // A root $ref forces the resolver to fetch and recursively resolve dependencies.
                var warmupSchema = $$"""
                {
                  "$ref": "{{url}}"
                }
                """;

                _ = await resolver.ResolveAsync(warmupSchema, url, auth: null).ConfigureAwait(false);
                _statusTracker.MarkSuccess();
                _logger.WarmupSucceeded(SchemaResolverService.SanitizeUrlForLogging(url));
            }
            catch (Exception ex)
            {
                _statusTracker.MarkFailure(url);
                _logger.WarmupFailed(ex, SchemaResolverService.SanitizeUrlForLogging(url));
            }
        }

        _statusTracker.MarkCompleted(stoppingToken.IsCancellationRequested);
        _logger.WarmupCompleted();
    }
}
