using System.ComponentModel;
using Newtonsoft.Json;

namespace OpenReferralApi.Core.Models.Configuration;

public enum HsdsValidationMode
{
    SpecAndFeedRuntimeFast,
    FullHsdsRuntime
}

/// <summary>
/// Configuration options for controlling OpenAPI validation and endpoint testing behavior
/// Allows fine-tuning of validation processes and testing parameters
/// </summary>
public class OpenApiValidationOptions : ValidationOptionsBase
{
    /// <summary>
    /// Whether to perform live endpoint testing against the API server
    /// Set to false for specification-only validation without HTTP requests
    /// Requires a valid BaseUrl in the request when enabled
    /// </summary>
    [JsonProperty("testEndpoints")]
    public bool TestEndpoints { get; set; } = true;

    /// <summary>
    /// Whether to test optional endpoints that are marked as optional in the OpenAPI specification
    /// When true, tests optional endpoints and accepts 404/501 responses as valid for unimplemented features
    /// When false, skips endpoints tagged with "Optional"
    /// </summary>
    [DefaultValue(true)]
    [JsonProperty("testOptionalEndpoints")]
    public bool TestOptionalEndpoints { get; set; } = true;

    /// <summary>
    /// Whether to report non-implemented optional endpoints as warnings instead of errors
    /// When true, optional endpoints returning 404/501 are logged as informational
    /// When false, all endpoint failures are treated as errors regardless of optional status
    /// </summary>
    [DefaultValue(true)]
    [JsonProperty("treatOptionalEndpointsAsWarnings")]
    public bool TreatOptionalEndpointsAsWarnings { get; set; } = true;

    /// <summary>
    /// Whether to include response bodies in `OpenApiValidationResult` output.
    /// When true, `HttpTestResult.responseBody` will contain the actual response content.
    /// When false (default), response bodies are omitted to reduce payload size and avoid exposing sensitive data.
    /// Must be explicitly set to true to include response bodies in validation results.
    /// </summary>
    [DefaultValue(false)]
    [JsonProperty("includeResponseBody")]
    public bool IncludeResponseBody { get; set; } = true;

    /// <summary>
    /// Whether to include detailed test results array in the EndpointTestResult output.
    /// When true, the full `TestResults` collection with all HTTP request/response details will be included.
    /// When false (default), the TestResults array will be excluded to reduce payload size.
    /// Must be explicitly set to true to include detailed test results in validation output.
    /// Note: This only affects the TestResults collection; summary information and validation errors are always included.
    /// </summary>
    [JsonProperty("includeTestResults")]
    public bool IncludeTestResults { get; set; } = true;

    /// <summary>
}

/// <summary>
/// Server-side OpenAPI validation settings not overridable by client payloads.
/// </summary>
public class OpenApiValidationServerOptions
{
    public const string SectionName = "OpenApiValidation";

    /// <summary>
    /// Whether to validate the OpenAPI specification structure and compliance.
    /// Includes schema validation, security analysis, and quality metrics.
    /// This is a server-side setting and cannot be overridden by client requests.
    /// </summary>
    public bool ValidateSpecification { get; set; } = true;

    /// <summary>
    /// Controls whether live feed responses are validated strictly against the feed's own schema.
    /// When true, any validation errors (including additional fields) are raised as errors.
    /// When false, validation failures are downgraded to warnings.
    /// </summary>
    public bool StrictOwnSchemaValidation { get; set; } = true;

    /// <summary>
    /// Selects the HSDS conformance depth.
    /// SpecAndFeedRuntimeFast performs strict feed-vs-own-spec runtime validation and feed-spec-vs-HSDS-spec comparison.
    /// FullHsdsRuntime additionally validates live feed responses against HSDS response schemas.
    /// </summary>
    [DefaultValue(HsdsValidationMode.SpecAndFeedRuntimeFast)]
    public HsdsValidationMode HsdsValidationMode { get; set; } = HsdsValidationMode.SpecAndFeedRuntimeFast;

    /// <summary>
    /// Whether to allow user-supplied authentication credentials for OpenAPI schema and data source requests.
    /// When enabled, authentication details provided in API requests will be used.
    /// When disabled, all requests are made without authentication.
    /// Default: false (for security)
    /// </summary>
    public bool AllowUserSuppliedAuth { get; set; } = false;
}
