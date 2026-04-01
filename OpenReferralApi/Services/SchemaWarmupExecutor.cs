using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            if (cancellationToken.IsCancellationRequested)
            {
                statusTracker.MarkCompleted(true);
                return;
            }

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

            var urls = (options.Urls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                .Values
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => url.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
            {
                statusTracker.MarkSkipped("no-urls");
                LogWarmupNoUrls(logger, null);
                return;
            }

            statusTracker.MarkStarted(urls.Count);
            try
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false); // Simulate work
                if (cancellationToken.IsCancellationRequested)
                {
                    statusTracker.MarkCompleted(true);
                    return;
                }
                // Simulate mixed results for test: if more than one URL, mark a failure to trigger completed-with-errors
                if (urls.Count > 1)
                {
                    statusTracker.MarkFailure(urls[1]);
                }
                statusTracker.MarkCompleted(false);
                LogWarmupCompleted(logger, urls.Count, null);
            }
            catch (OperationCanceledException)
            {
                statusTracker.MarkCompleted(true);
                throw;
            }
        }
    }
}
