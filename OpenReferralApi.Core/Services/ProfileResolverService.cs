namespace OpenReferralApi.Core.Services;

public interface IProfileResolverService
{
    ProfileResolutionDecision Resolve(
        string? profileReason,
        string? schemaUrl,
        bool hasConfiguredDefaultProfile,
        string? defaultProfileSchemaUrl,
        string? defaultProfileVersion);
}

public sealed class ProfileResolutionDecision
{
    public string? ClaimedProfileVersion { get; init; }
    public string? KnownHsdsSchemaUrl { get; init; }
    public string? EffectiveProfileReason { get; init; }
}

public sealed class ProfileResolverService : IProfileResolverService
{
    private readonly IHsdsComplianceService _hsdsComplianceService;

    public ProfileResolverService(IHsdsComplianceService hsdsComplianceService)
    {
        _hsdsComplianceService = hsdsComplianceService;
    }

    public ProfileResolutionDecision Resolve(
        string? profileReason,
        string? schemaUrl,
        bool hasConfiguredDefaultProfile,
        string? defaultProfileSchemaUrl,
        string? defaultProfileVersion)
    {
        var effectiveProfileReason = profileReason;
        var claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(profileReason, schemaUrl);
        string? knownHsdsSchemaUrl = null;

        if (string.IsNullOrWhiteSpace(claimedProfileVersion)
            && hasConfiguredDefaultProfile
            && !string.IsNullOrWhiteSpace(defaultProfileSchemaUrl))
        {
            knownHsdsSchemaUrl = defaultProfileSchemaUrl;
            if (!string.IsNullOrWhiteSpace(defaultProfileVersion))
            {
                effectiveProfileReason = $"Standard version [user: {defaultProfileVersion}] configured default profile fallback";
            }

            claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(effectiveProfileReason, schemaUrl);
        }

        if (string.IsNullOrWhiteSpace(knownHsdsSchemaUrl)
            && _hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out var mappedKnownHsdsSchemaUrl)
            && !string.IsNullOrWhiteSpace(mappedKnownHsdsSchemaUrl))
        {
            knownHsdsSchemaUrl = mappedKnownHsdsSchemaUrl;
        }

        return new ProfileResolutionDecision
        {
            ClaimedProfileVersion = claimedProfileVersion,
            KnownHsdsSchemaUrl = knownHsdsSchemaUrl,
            EffectiveProfileReason = effectiveProfileReason
        };
    }
}
