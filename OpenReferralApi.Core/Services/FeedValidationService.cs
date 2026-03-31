using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;
using OpenReferralApi.Core.Models;

namespace OpenReferralApi.Core.Services;

/// <summary>
/// Service for validating registered feeds and updating their status
/// </summary>
public interface IFeedValidationService
{
  Task<List<ServiceFeed>> GetAllFeedsAsync(CancellationToken cancellationToken = default);
  Task UpdateFeedStatusAsync(string feedId, bool isUp, bool isValid, string? error, double? responseTimeMs, int? validationErrorCount, CancellationToken cancellationToken = default);
  Task<FeedValidationResult> ValidateSingleFeedAsync(ServiceFeed feed, CancellationToken cancellationToken = default);
}

public partial class FeedValidationService : IFeedValidationService
{
  private readonly IMongoCollection<ServiceFeed> _servicesCollection;
  private readonly IOpenApiValidationService _validationService;
  private readonly ILogger<FeedValidationService> _logger;

  public FeedValidationService(
      IMongoClient mongoClient,
      IOptions<DatabaseOptions> databaseOptions,
      IOpenApiValidationService validationService,
      ILogger<FeedValidationService> logger)
  {
    var database = mongoClient.GetDatabase(databaseOptions.Value.DatabaseName);
    _servicesCollection = database.GetCollection<ServiceFeed>(databaseOptions.Value.ServicesCollection);
    _validationService = validationService;
    _logger = logger;
  }

  public async Task<List<ServiceFeed>> GetAllFeedsAsync(CancellationToken cancellationToken = default)
  {
    try
    {
      // Filter for active feeds - check for boolean true, string "true", or nested value
      var filter = Builders<ServiceFeed>.Filter.Or(
          Builders<ServiceFeed>.Filter.Eq(f => f.ActiveField, true),
          Builders<ServiceFeed>.Filter.Eq(f => f.ActiveField, "true"),
          Builders<ServiceFeed>.Filter.Regex("active.value", new System.Text.RegularExpressions.Regex("^true$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
      );

      return await _servicesCollection
          .Find(filter)
          .ToListAsync(cancellationToken);
    }
    catch (Exception ex)
    {
      LogRetrieveFeedsFailed(ex);
      return new List<ServiceFeed>();
    }
  }

  public async Task UpdateFeedStatusAsync(
      string feedId,
      bool isUp,
      bool isValid,
      string? error,
      double? responseTimeMs,
      int? validationErrorCount,
      CancellationToken cancellationToken = default)
  {
    try
    {
      var filter = Builders<ServiceFeed>.Filter.Eq(f => f.Id, feedId);

      // Read the current document to check structure of existing status fields
      var currentFeed = await _servicesCollection.Find(filter).FirstOrDefaultAsync(cancellationToken);
      if (currentFeed == null)
      {
        LogFeedNotFoundForUpdate(feedId);
        return;
      }

      var updateBuilder = Builders<ServiceFeed>.Update;
      var updates = new List<UpdateDefinition<ServiceFeed>>();

      // For status fields, if they exist as BsonDocuments, update nested value field
      // otherwise set as simple boolean
      if (currentFeed.StatusIsUp?.IsBsonDocument ?? false)
      {
        updates.Add(updateBuilder.Set("statusIsUp.value", isUp));
      }
      else
      {
        updates.Add(updateBuilder.Set(f => f.StatusIsUp, isUp));
      }

      if (currentFeed.StatusIsValid?.IsBsonDocument ?? false)
      {
        updates.Add(updateBuilder.Set("statusIsValid.value", isValid));
      }
      else
      {
        updates.Add(updateBuilder.Set(f => f.StatusIsValid, isValid));
      }

      if (currentFeed.StatusOverall?.IsBsonDocument ?? false)
      {
        updates.Add(updateBuilder.Set("statusOverall.value", isValid));
      }
      else
      {
        updates.Add(updateBuilder.Set(f => f.StatusOverall, isValid));
      }

      updates.Add(updateBuilder.Set(f => f.LastChecked, DateTime.UtcNow));
      updates.Add(updateBuilder.Set(f => f.LastError, error));
      updates.Add(updateBuilder.Set(f => f.ResponseTimeMs, responseTimeMs));
      updates.Add(updateBuilder.Set(f => f.ValidationErrorCount, validationErrorCount));

      // Update lastTested with current timestamp and results URL
      var lastTestedDoc = new BsonDocument
      {
        { "value", DateTime.UtcNow },
        { "url", $"/developers/dashboard/{feedId}" }
      };
      updates.Add(updateBuilder.Set(f => f.LastTested, lastTestedDoc));

      var combinedUpdate = updateBuilder.Combine(updates);
      await _servicesCollection.UpdateOneAsync(filter, combinedUpdate, cancellationToken: cancellationToken);

      LogFeedStatusUpdated(feedId, isUp, isValid, responseTimeMs, validationErrorCount);
    }
    catch (Exception ex)
    {
      LogUpdateFeedStatusFailed(ex, feedId);
    }
  }

  public async Task<FeedValidationResult> ValidateSingleFeedAsync(
      ServiceFeed feed,
      CancellationToken cancellationToken = default)
  {
    var result = new FeedValidationResult
    {
      FeedId = feed.Id!,
      FeedUrl = feed.Url,
      FeedName = feed.NameAsString
    };

    try
    {
      LogValidatingFeed(feed.NameAsString ?? "Unnamed", feed.Url);

      var validationRequest = new OpenApiValidationRequest
      {
        BaseUrl = feed.Url,
        Options = new OpenApiValidationOptions
        {
          ValidateSpecification = false,
          TestEndpoints = true,
          TestOptionalEndpoints = true,
          TreatOptionalEndpointsAsWarnings = true,
          TimeoutSeconds = 60,
          MaxConcurrentRequests = 10,
          ReportAdditionalFields = false
        }
      };

      var validationResult = await _validationService.ValidateOpenApiSpecificationAsync(
          validationRequest,
          cancellationToken);

      // IsUp is true if any endpoint test result was successful
      result.IsUp = validationResult.EndpointTests
          .SelectMany(e => e.TestResults)
          .Any(tr => tr.IsSuccessStatusCode);
      result.IsValid = validationResult.IsValid;
      result.ResponseTimeMs = validationResult.Duration.TotalMilliseconds;
      
      // Count all validation errors from endpoint test results
      result.ValidationErrorCount = validationResult.EndpointTests
          .Sum(e => e.ValidationErrors.Count);

      if (!validationResult.IsValid)
      {
        // Extract error messages from endpoint test results
        var errors = validationResult.EndpointTests
          .SelectMany(e => e.ValidationErrors)
            .Take(5)
            .Select(e => $"{e.Path}: {e.Message}")
            .ToList();

        result.ErrorMessage = errors.Any()
            ? string.Join("; ", errors)
            : "Validation failed with no specific errors";
      }

      LogFeedValidationCompleted(feed.NameAsString ?? "Unnamed", result.IsUp, result.IsValid, result.ValidationErrorCount);
    }
    catch (HttpRequestException ex)
    {
      LogFeedNotAccessible(ex, feed.Url);
      result.IsUp = false;
      result.IsValid = false;
      result.ErrorMessage = $"HTTP error: {SanitizeExceptionMessage(ex.Message)}";
    }
    catch (TaskCanceledException ex)
    {
      LogFeedValidationTimedOut(ex, feed.Url);
      result.IsUp = false;
      result.IsValid = false;
      result.ErrorMessage = "Request timed out";
    }
    catch (Exception ex)
    {
      LogFeedValidationUnexpectedError(ex, feed.Url);
      result.IsUp = false;
      result.IsValid = false;
      result.ErrorMessage = $"Unexpected error: {SanitizeExceptionMessage(ex.Message)}";
    }

    return result;
  }

  /// <summary>
  /// Sanitizes exception messages to prevent log injection attacks by removing control characters.
  /// </summary>
  private static string SanitizeExceptionMessage(string message)
  {
    if (string.IsNullOrEmpty(message))
      return string.Empty;

    // Remove control characters (including CR/LF) to prevent log forging
    var sanitized = new string(message.Where(c => !char.IsControl(c)).ToArray());

    // Limit length to prevent log flooding
    const int maxLength = 500;
    if (sanitized.Length > maxLength)
    {
      sanitized = sanitized.Substring(0, maxLength) + "...(truncated)";
    }

    return sanitized;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to retrieve feeds from database")]
  private partial void LogRetrieveFeedsFailed(Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Feed {FeedId} not found for update")]
  private partial void LogFeedNotFoundForUpdate(string feedId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Updated feed {FeedId}: IsUp={IsUp}, IsValid={IsValid}, ResponseTime={ResponseTime}ms, Errors={ErrorCount}")]
  private partial void LogFeedStatusUpdated(string feedId, bool isUp, bool isValid, double? responseTime, int? errorCount);

  [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to update feed status for feed {FeedId}")]
  private partial void LogUpdateFeedStatusFailed(Exception ex, string feedId);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Validating feed: {FeedName} ({FeedUrl})")]
  private partial void LogValidatingFeed(string feedName, string feedUrl);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Feed validation completed: {FeedName} - IsUp={IsUp}, IsValid={IsValid}, Errors={ErrorCount}")]
  private partial void LogFeedValidationCompleted(string feedName, bool isUp, bool isValid, int errorCount);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Feed is not accessible: {FeedUrl}")]
  private partial void LogFeedNotAccessible(Exception ex, string feedUrl);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Feed validation timed out: {FeedUrl}")]
  private partial void LogFeedValidationTimedOut(Exception ex, string feedUrl);

  [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Unexpected error validating feed: {FeedUrl}")]
  private partial void LogFeedValidationUnexpectedError(Exception ex, string feedUrl);
}

/// <summary>
/// Null implementation when MongoDB is not configured
/// </summary>
public partial class NullFeedValidationService : IFeedValidationService
{
  private readonly ILogger<NullFeedValidationService> _logger;

  public NullFeedValidationService(ILogger<NullFeedValidationService> logger)
  {
    _logger = logger;
  }

  public Task<List<ServiceFeed>> GetAllFeedsAsync(CancellationToken cancellationToken = default)
  {
    LogServiceNotAvailable();
    return Task.FromResult(new List<ServiceFeed>());
  }

  public Task UpdateFeedStatusAsync(string feedId, bool isUp, bool isValid, string? error, double? responseTimeMs, int? validationErrorCount, CancellationToken cancellationToken = default)
  {
    LogServiceNotAvailable();
    return Task.CompletedTask;
  }

  public Task<FeedValidationResult> ValidateSingleFeedAsync(ServiceFeed feed, CancellationToken cancellationToken = default)
  {
    LogServiceNotAvailable();
    return Task.FromResult(new FeedValidationResult
    {
      FeedId = feed.Id ?? string.Empty,
      FeedUrl = feed.Url,
      FeedName = feed.NameAsString,
      IsUp = false,
      IsValid = false,
      ErrorMessage = "Feed validation service is not available. MongoDB is not configured."
    });
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Feed validation service is not available. MongoDB is not configured.")]
  private partial void LogServiceNotAvailable();
}

/// <summary>
/// Result of validating a single feed
/// </summary>
public class FeedValidationResult
{
  public string FeedId { get; set; } = string.Empty;
  public string FeedUrl { get; set; } = string.Empty;
  public string? FeedName { get; set; }
  public bool IsUp { get; set; }
  public bool IsValid { get; set; }
  public string? ErrorMessage { get; set; }
  public double? ResponseTimeMs { get; set; }
  public int ValidationErrorCount { get; set; }
}
