# Schema Caching Changes

This document summarizes the schema caching improvements in the API and how they behave in production.

## Summary of Changes

1. Added persistent in-memory caching for remote schema fetches.
2. Kept fast per-request reference caching for already-resolved refs.
3. Added startup schema warmup to pre-populate cache entries.
4. Added warmup status reporting through the live health endpoint.
5. Added URL normalization for known JSON Schema draft URLs to reduce duplicate fetches.
6. Made all behavior configurable through `Cache`, `SchemaResolution`, and `SchemaWarmup` settings.

## Before vs After

Before:
- Schema references could trigger repeated remote HTTP calls across requests.
- No startup pre-loading of frequently used schemas.
- No explicit warmup status visibility in health payloads.

After:
- Remote schemas are cached in process and reused across requests.
- Common schemas are fetched once shortly after startup (warmup).
- Live health output includes schema warmup progress and outcome.

## Caching Architecture

There are now two cache scopes:

### 1) Resolution-session cache (short-lived)

- Implemented by `ReferenceResolver` using an internal dictionary (`_refCache`).
- Scope: one schema resolution run.
- Purpose: avoid re-resolving the same `$ref` repeatedly during recursion.

### 2) Application memory cache (persistent)

- Implemented by `RemoteSchemaLoader` using `IMemoryCache`.
- Scope: across requests for the lifetime of the process.
- Purpose: avoid repeated outbound HTTP GET calls for the same schema URL.
- Cache key format: `schema:<resolved-url>`.
- Entries are stored as schema JSON text and re-parsed when read.

## URL Handling and Cache-Key Behavior

To increase cache hit rate and avoid duplicate entries for equivalent URLs:

- Known JSON Schema draft URLs are normalized to absolute-path form
  (query string and fragment removed, trailing slash trimmed).
- OpenReferral spec URLs may be rewritten to a local spec base URL in development.
- Cache keys are based on the rewritten/normalized URL actually used for fetch.

This means logically equivalent schema URLs are less likely to create separate cache records.

## Configuration

### Cache settings (`Cache`)

- `Enabled`: global on/off switch for remote schema memory caching.
- `ExpirationMinutes`: absolute expiration window.
- `UseSlidingExpiration`: enables sliding expiration.
- `SlidingExpirationMinutes`: sliding window when enabled.
- `MaxSizeMB`: total memory-cache size budget (configured at startup).

Notes:
- If `Enabled` is `false`, schemas are fetched remotely each time they are needed.
- If `ExpirationMinutes` is `0`, no explicit time-based expiration is applied.

### Schema warmup settings (`SchemaWarmup`)

- `Enabled`: enables warmup hosted service.
- `StartupDelaySeconds`: delay before warmup begins.
- `Urls`: list of root schema URLs to pre-load.

Warmup only runs when both `SchemaWarmup.Enabled` and `Cache.Enabled` are true.

### Schema resolution settings (`SchemaResolution`)

- `KnownJsonSchemaUrls`: allow-list used for known draft URL normalization.
- `WarnOnUnknownJsonSchemaDraft`: logs warnings for unknown json-schema draft URLs.

## Warmup Behavior

Warmup runs once in a background hosted service:

1. Waits for optional startup delay.
2. Deduplicates configured warmup URLs.
3. For each URL, resolves a synthetic root schema referencing that URL.
4. This triggers recursive remote schema loading and cache population.
5. Logs success/failure per URL.

Failure model:
- Warmup failures are non-fatal and do not block application startup.
- Failures are logged as warnings.

## Observability

The live health endpoint payload includes schema warmup status:

- Whether warmup started/completed/skipped.
- Total URL count and succeeded count.
- Failed URLs.
- Skip reason (for example: disabled, cache-disabled, no-urls).

This makes it easier to confirm cache priming after deploy.

## Operational Impact

Expected improvements:

- Lower schema-validation latency after warmup.
- Fewer outbound requests to external schema hosts.
- Better resilience during transient external schema-host slowness.

Trade-offs:

- Uses process memory for cached schema bodies.
- Cache contents are process-local (not shared across instances).
- Cache is lost on restart and re-primed by warmup/runtime traffic.

## Rollout Guidance

1. Keep `Cache.Enabled=true` in environments where remote schema calls are expected.
2. Configure `SchemaWarmup.Urls` to include the most common root schemas.
3. Set `MaxSizeMB` based on host memory constraints.
4. Tune `ExpirationMinutes` and `SlidingExpirationMinutes` for traffic patterns.
5. Check live health output after deployment to confirm warmup completion.
