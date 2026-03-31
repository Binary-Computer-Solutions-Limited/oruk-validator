using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenReferralApi.Core.Models;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Controllers;

[ApiController]
[Route("openreferraluk")]
[Route("api/openapi")] // Legacy route for backward compatibility, will be removed in future versions (once openreferraluk website is updated to point to new route)
[Produces("application/json")]
[EnableRateLimiting("fixed")]
[ApiExplorerSettings(GroupName = "v1")]
public partial class OpenReferralUkController : BaseOpenApiController
{
    private readonly IOpenApiValidationService _openApiValidationService;
    private readonly ILogger<OpenReferralUkController> _logger;
    private readonly IOpenReferralUKValidationResponseMapper _mapper;

    public OpenReferralUkController(
        IOpenApiValidationService openApiValidationService,
        ILogger<OpenReferralUkController> logger,
        IOpenReferralUKValidationResponseMapper mapper)
    {
        _openApiValidationService = openApiValidationService;
        _logger = logger;
        _mapper = mapper;
    }

    /// <summary>
    /// Validates an OpenAPI specification and tests all defined endpoints, returning Open Referral UK formatted results
    /// </summary>
    /// <param name="request">The validation request containing OpenAPI URL and base URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation results mapped to Open Referral UK format</returns>
    /// <response code="200">Validation completed successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="429">Rate limit exceeded</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(OpenReferralUKValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<OpenReferralUKValidationResponse>> ValidateAsync(
        [FromBody] OpenApiValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        LogReceivedValidationRequestUk(
            SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty));

        var validationError = ValidateRequestAndReturnErrorIfInvalid(request);
        if (validationError != null)
        {
            return validationError;
        }

        var result = await _openApiValidationService.ValidateOpenApiSpecificationAsync(request, cancellationToken);
        
        LogValidationCompletedUk(
            SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty));

        var mappedResult = _mapper.MapToOpenReferralUKValidationResponse(result);
        return Ok(mappedResult);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Received OpenAPI validation request (Open Referral UK format) for BaseUrl: {BaseUrl}")]
    private partial void LogReceivedValidationRequestUk(string baseUrl);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Validation completed (Open Referral UK format) for BaseUrl: {BaseUrl}")]
    private partial void LogValidationCompletedUk(string baseUrl);
}
