using System.Threading;
using Json.Schema;
using Json.Schema.OpenApi;

namespace OpenReferralApi.Core.Helpers;

internal static class JsonSchemaBuild
{
    private static readonly Lazy<bool> OpenApiMetaSchemaRegistration = new(() =>
    {
        try
        {
            Json.Schema.OpenApi.MetaSchemas.Register();
        }
        catch (ArgumentException ex) when (ex.Message.Contains("same key has already been added", StringComparison.OrdinalIgnoreCase))
        {
            // Registration is global; if another startup path got there first, continue.
        }

        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly BuildOptions OpenApiBuildOptions = new()
    {
        // Draft 2020-12 allows extension keywords used by OpenAPI profiles.
        Dialect = Json.Schema.Dialect.Draft202012
    };

    public static JsonSchema FromText(string schemaJson)
    {
        EnsureInitialized();
        return JsonSchema.FromText(schemaJson, OpenApiBuildOptions);
    }

    private static void EnsureInitialized()
    {
        _ = OpenApiMetaSchemaRegistration.Value;
    }
}