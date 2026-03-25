# Change Set 2: Helper-Class Unit Test Coverage

## Scope
This document captures the direct unit tests added for the newly introduced internal helper classes.

## Testability Enablement
- File: `OpenReferralApi.Core/GlobalUsings.cs` (new)
- Change:
  - Added `InternalsVisibleTo("OpenReferralApi.Tests")`.
- Outcome:
  - Allows test project to instantiate and verify `internal` helper classes directly.

## New Test Suites

### 1) RemoteSchemaLoader security and validation tests
- File: `OpenReferralApi.Tests/Services/RemoteSchemaLoaderTests.cs` (new)
- Coverage areas:
  - Header name hardening (control characters, colon, malformed header names).
  - API key header validation and skip behavior for invalid header names.
  - SSRF-style URL scheme restrictions (`file://`, `ftp://`, `data:` rejected).
  - Auth validation edge cases:
    - API key without header name.
    - Basic auth with empty/null password.
    - Empty auth object.
    - Valid basic auth path.
  - Cache behavior checks with enabled and sliding-expiration configuration.

### 2) OpenApiSpecFetcher validation and flow tests
- File: `OpenReferralApi.Tests/Services/OpenApiSpecFetcherTests.cs` (new)
- Coverage areas:
  - Auth filtering behavior for empty/partial credentials.
  - Custom headers and null-auth behavior.
  - Invalid and relative URL handling (wrapped exception behavior verified).
  - `resolveReferences` switch behavior:
    - true -> schema resolver invoked.
    - false -> schema resolver bypassed.

### 3) ReferenceResolver circular/ref-resolution tests
- File: `OpenReferralApi.Tests/Services/ReferenceResolverTests.cs` (new)
- Coverage areas:
  - Circular reference handling:
    - Direct self-reference.
    - Indirect cycles.
    - Multi-hop cycles.
    - External cycle patterns.
  - Internal JSON pointer resolution:
    - object paths.
    - array index paths.
    - escaped pointer segments (`~0`, `~1`).
  - Composite schema merging:
    - `allOf` merge behavior.
    - merged result with referenced definitions.

## Validation Status
- Targeted helper tests added: 39 tests.
- Full suite status after addition:
  - Total tests: 232
  - Passed: 232
  - Failed: 0

## Net Result
- Direct, explicit coverage for security-critical helper logic.
- Better regression protection around auth filtering, URL validation, and ref-resolution edge cases.
- Confidence increased for the decomposed architecture introduced in Phase 2c.
