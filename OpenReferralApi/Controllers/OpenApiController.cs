using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Controllers;

[ApiController]
[Route("openreferral")]
[Produces("application/json")]
[EnableRateLimiting("fixed")]
public class OpenReferralController : BaseOpenApiController
{
    private readonly IOpenApiValidationService _openApiValidationService;
    private readonly ILogger<OpenReferralController> _logger;

    public OpenReferralController(
        IOpenApiValidationService openApiValidationService,
        ILogger<OpenReferralController> logger)
    {
        _openApiValidationService = openApiValidationService;
        _logger = logger;
    }

    /// <summary>
    /// Validates an OpenAPI specification and tests all defined endpoints, returning raw results
    /// </summary>
    /// <param name="request">The validation request containing OpenAPI URL and base URL</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Raw validation results</returns>
    /// <response code="200">Validation completed successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="429">Rate limit exceeded</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(OpenApiValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<OpenApiValidationResult>> ValidateAsync(
        [FromBody] OpenApiValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Received OpenAPI validation request for BaseUrl: {BaseUrl}",
            SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty));

        var validationError = ValidateRequestAndReturnErrorIfInvalid(request);
        if (validationError != null)
        {
            return validationError;
        }

        var result = await _openApiValidationService.ValidateOpenApiSpecificationAsync(request, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Validation completed for BaseUrl: {BaseUrl}",
            SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty));

        return Ok(result);
    }
}
