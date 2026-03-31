using Microsoft.Extensions.Logging;

namespace OpenReferralApi.Core.Logging
{
    public static partial class ProfileDiscoveryLog
    {
        [LoggerMessage(EventId = 12000, Level = LogLevel.Information, Message = "Requesting BaseUrl to discover openapi_url: {BaseUrl}")]
        public static partial void RequestingBaseUrl(this ILogger logger, string baseUrl);

        [LoggerMessage(EventId = 12001, Level = LogLevel.Information, Message = "BaseUrl request returned {Status}; unable to determine HSDS schema version")]
        public static partial void BaseUrlRequestFailed(this ILogger logger, int status);

        [LoggerMessage(EventId = 12002, Level = LogLevel.Information, Message = "Discovered openapi_url: {OpenApiUrl}")]
        public static partial void DiscoveredOpenApiUrl(this ILogger logger, string openApiUrl);

        [LoggerMessage(EventId = 12003, Level = LogLevel.Information, Message = "Detected version '{Version}'; resolved spec: {OpenApiUrl}")]
        public static partial void DetectedVersionResolvedSpec(this ILogger logger, string version, string openApiUrl);

        [LoggerMessage(EventId = 12004, Level = LogLevel.Information, Message = "No openapi_url or version in BaseUrl response; unable to determine HSDS schema version")]
        public static partial void NoOpenApiUrlOrVersionFound(this ILogger logger);

        [LoggerMessage(EventId = 12005, Level = LogLevel.Warning, Message = "Failed to parse JSON from BaseUrl response; unable to determine HSDS schema version")]
        public static partial void FailedToParseBaseUrlJson(this ILogger logger, Exception exception);

        [LoggerMessage(EventId = 12006, Level = LogLevel.Warning, Message = "Error requesting BaseUrl to discover openapi_url; unable to determine HSDS schema version")]
        public static partial void ErrorRequestingBaseUrl(this ILogger logger, Exception exception);
    }
}
