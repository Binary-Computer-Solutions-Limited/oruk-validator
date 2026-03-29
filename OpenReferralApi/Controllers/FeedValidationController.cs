using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenReferralApi.Core.Services;
using OpenReferralApi.Models;

namespace OpenReferralApi.Controllers;

/// <summary>
/// Controller for managing and testing feed validation
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("fixed")]
public class FeedValidationController : ControllerBase
{
  private readonly IFeedValidationService _feedValidationService;
  private readonly ILogger<FeedValidationController> _logger;

  public FeedValidationController(
      IFeedValidationService feedValidationService,
      ILogger<FeedValidationController> logger)
  {
    _feedValidationService = feedValidationService;
    _logger = logger;
  }

  /// <summary>
  /// Get all registered feeds with their current status
  /// </summary>
  /// <returns>List of all feeds</returns>
  [HttpGet("feeds")]
  [ProducesResponseType(typeof(List<ServiceFeed>), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
  public async Task<ActionResult<List<ServiceFeed>>> GetAllFeeds(CancellationToken cancellationToken)
  {
    var feeds = await _feedValidationService.GetAllFeedsAsync(cancellationToken);
    return Ok(feeds);
  }

  /// <summary>
  /// Manually trigger validation for all feeds
  /// </summary>
  /// <returns>Validation results for all feeds</returns>
  [HttpPost("validate-all")]
  [ProducesResponseType(typeof(FeedValidationSummary), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
  public async Task<ActionResult<FeedValidationSummary>> ValidateAllFeeds(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Manual validation triggered for all feeds");

    var feeds = await _feedValidationService.GetAllFeedsAsync(cancellationToken);

    if (feeds.Count == 0)
    {
      return Ok(new FeedValidationSummary
      {
        TotalFeeds = 0,
        Message = "No feeds found in database"
      });
    }
    var results = await _feedValidationService.ValidateAndUpdateFeedsAsync(feeds, cancellationToken: cancellationToken);

    var summary = new FeedValidationSummary
    {
      TotalFeeds = feeds.Count,
      UpFeeds = results.Count(r => r.IsUp),
      ValidFeeds = results.Count(r => r.IsValid),
      DownFeeds = results.Count(r => !r.IsUp),
      InvalidFeeds = results.Count(r => r.IsUp && !r.IsValid),
      AverageResponseTimeMs = results.Where(r => r.ResponseTimeMs.HasValue)
            .Average(r => r.ResponseTimeMs),
      Results = results
    };

    _logger.LogInformation(
        "Manual validation completed: {Total} feeds, {Up} up, {Valid} valid",
        summary.TotalFeeds, summary.UpFeeds, summary.ValidFeeds);

    return Ok(summary);
  }

  /// <summary>
  /// Manually trigger validation for a specific feed by ID
  /// </summary>
  /// <param name="feedId">The MongoDB ObjectId of the feed</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Validation result for the specified feed</returns>
  [HttpPost("validate/{feedId}")]
  [ProducesResponseType(typeof(FeedValidationResult), StatusCodes.Status200OK)]
  [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
  [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
  public async Task<ActionResult<FeedValidationResult>> ValidateFeed(
      string feedId,
      CancellationToken cancellationToken)
  {
    var feeds = await _feedValidationService.GetAllFeedsAsync(cancellationToken);
    var feed = feeds.FirstOrDefault(f => f.Id == feedId);

    if (feed == null)
    {
      return NotFound(new ApiErrorResponse
      {
        Error = "Feed not found",
        FeedId = feedId
      });
    }

    var safeFeedId = feedId?.Replace("\r", string.Empty).Replace("\n", string.Empty);
    _logger.LogInformation("Manual validation triggered for feed {FeedId}", safeFeedId);

    var result = await _feedValidationService.ValidateAndUpdateFeedAsync(feed, cancellationToken);

    return Ok(result);
  }
}

/// <summary>
/// Summary of feed validation results
/// </summary>
public class FeedValidationSummary
{
  public int TotalFeeds { get; set; }
  public int UpFeeds { get; set; }
  public int ValidFeeds { get; set; }
  public int DownFeeds { get; set; }
  public int InvalidFeeds { get; set; }
  public double? AverageResponseTimeMs { get; set; }
  public string? Message { get; set; }
  public List<FeedValidationResult> Results { get; set; } = new();
}
