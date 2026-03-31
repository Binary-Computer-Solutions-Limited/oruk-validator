using Microsoft.Extensions.Logging;

namespace OpenReferralApi.Core.Logging
{
    public static partial class SchemaWarmupLog
    {
        [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Schema warmup is disabled.")]
        public static partial void WarmupDisabled(this ILogger logger);

        [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Schema warmup is skipped because cache is disabled.")]
        public static partial void WarmupSkippedCacheDisabled(this ILogger logger);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Schema warmup is enabled but no URLs are configured.")]
        public static partial void WarmupNoUrlsConfigured(this ILogger logger);

        [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Schema warmup starting in {DelaySeconds}s.")]
        public static partial void WarmupStartingIn(this ILogger logger, int delaySeconds);

        [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Starting schema warmup for {Count} URL(s).")]
        public static partial void WarmupStartingForUrls(this ILogger logger, int count);

        [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Schema warmup succeeded: {SchemaUrl}")]
        public static partial void WarmupSucceeded(this ILogger logger, string schemaUrl);

        [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "Schema warmup failed: {SchemaUrl}")]
        public static partial void WarmupFailed(this ILogger logger, Exception exception, string schemaUrl);

        [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "Schema warmup completed.")]
        public static partial void WarmupCompleted(this ILogger logger);
    }
}
