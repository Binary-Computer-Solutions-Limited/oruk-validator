using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenReferralApi.Core.Services;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Services
{
    internal interface ISchemaWarmupExecutor
    {
        Task WarmupAsync(
            SpecificationOptions options,
            CacheOptions cacheOptions,
            ISchemaWarmupStatusTracker statusTracker,
            IServiceProvider serviceProvider,
            ILogger logger,
            CancellationToken cancellationToken);
    }

    internal sealed class SchemaWarmupExecutor : ISchemaWarmupExecutor
    {
        private static readonly Action<ILogger, Exception?> LogWarmupDisabled =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(LogWarmupDisabled)), "Warmup disabled");

        private static readonly Action<ILogger, Exception?> LogWarmupCacheDisabled =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(LogWarmupCacheDisabled)), "Warmup skipped: cache disabled");

        private static readonly Action<ILogger, Exception?> LogWarmupNoUrls =
            LoggerMessage.Define(LogLevel.Information, new EventId(3, nameof(LogWarmupNoUrls)), "Warmup: no URLs configured");

        private static readonly Action<ILogger, int, Exception?> LogWarmupCompleted =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(4, nameof(LogWarmupCompleted)), "Warmup completed for {UrlCount} URLs");

        public async Task WarmupAsync(
            SpecificationOptions options,
            CacheOptions cacheOptions,
            ISchemaWarmupStatusTracker statusTracker,
            IServiceProvider serviceProvider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var urls = (options.Urls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .Values
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!options.WarmupEnabled)
            {
                statusTracker.MarkSkipped("disabled");
                LogWarmupDisabled(logger, null);
                return;
            }

            if (!cacheOptions.Enabled)
            {
                statusTracker.MarkSkipped("cache-disabled");
                LogWarmupCacheDisabled(logger, null);
                return;
            }

            if (urls.Count == 0)
            {
                statusTracker.MarkSkipped("no-urls");
                LogWarmupNoUrls(logger, null);
                return;
            }

            statusTracker.MarkStarted(urls.Count);
            try
            {
                if (options.WarmupStartupDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.WarmupStartupDelaySeconds), cancellationToken).ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    statusTracker.MarkCompleted(true);
                    return;
                }

                using var scope = serviceProvider.CreateScope();
                var resolver = scope.ServiceProvider.GetRequiredService<ISchemaResolverService>();

                foreach (var url in urls)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        statusTracker.MarkCompleted(true);
                        return;
                    }

                    var warmupSchemaRef = $$"""
                    {
                      "$ref": "{{url}}"
                    }
                    """;

                    try
                    {
                        await resolver.ResolveAsync(warmupSchemaRef, url, auth: null).ConfigureAwait(false);
                        statusTracker.MarkSuccess();
                    }
                    catch (InvalidOperationException)
                    {
                        statusTracker.MarkFailure(url);
                    }
                    catch (HttpRequestException)
                    {
                        statusTracker.MarkFailure(url);
                    }
                    catch (UriFormatException)
                    {
                        statusTracker.MarkFailure(url);
                    }
                    catch (TaskCanceledException)
                    {
                        statusTracker.MarkFailure(url);
                    }
                    catch (ArgumentException)
                    {
                        statusTracker.MarkFailure(url);
                    }
                }

                statusTracker.MarkCompleted(false);
                LogWarmupCompleted(logger, urls.Count, null);
            }
            catch (OperationCanceledException)
            {
                statusTracker.MarkCompleted(true);
            }
        }
    }
}
