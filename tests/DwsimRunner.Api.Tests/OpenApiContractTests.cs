// The generated OpenAPI document, and what stops it lying (ISK-231).
//
// A generated spec is only worth more than hand-written prose if it cannot quietly disagree with
// the service. Three ways it could, each with a test here:
//
//   1. A NEW ROUTE lands with no metadata. Swashbuckle would list it with no summary and no
//      response schema — present in the document, describing nothing. RouteMetadata... below
//      fails on any route missing either.
//
//   2. A FIELD DESCRIPTION silently vanishes. `///` comments inside a record's parameter list
//      compile with a CS1587 warning and are DISCARDED, so the source looks documented and the
//      document is bare. That is exactly what happened while writing this; Descriptions_reach...
//      pins a known one so it cannot happen again unnoticed.
//
//   3. A RESPONSE RECORD drifts from what the worker sends. Response bodies are worker
//      pass-through — the API never deserializes them — so nothing at runtime would notice a
//      field the schema does not name. Declared_response_shapes... round-trips real worker
//      output through the record and fails on any dropped field.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class OpenApiContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// Every route this service registers. A new route must be added here deliberately —
    /// the count assertion below is what makes "I added an endpoint and documented nothing" a
    /// build failure rather than a silent omission.
    public static readonly string[] ExpectedPaths =
    [
        "/health",
        "/templates",
        "/templates/{id}",
        "/templates/{id}/file",
        "/templates/{id}/objects",
        "/templates/{id}/pfd.png",
        "/catalog/compounds",
        "/catalog/property-packages",
        "/catalog/unit-op-types",
        "/catalog/engine-inventory",
        "/catalog/units",
        "/flowsheets/validate",
        "/flowsheets/build-solve",
        "/flowsheets/pfd",
        "/flash",
        "/solve",
        "/compare",
        "/optimize",
    ];

    private static async Task<JsonElement> SpecAsync(RunnerHost host)
    {
        var resp = await host.Client.GetAsync("/openapi.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    [Fact]
    public async Task Spec_is_served_and_lists_every_route()
    {
        using var host = new RunnerHost();
        var spec = await SpecAsync(host);

        var paths = spec.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();

        // Both directions. Missing = a route nobody can discover; extra = a route that shipped
        // without anyone deciding to document it.
        Assert.True(!ExpectedPaths.Except(paths).Any(),
            "declared but not in the spec: " + string.Join(", ", ExpectedPaths.Except(paths)));
        Assert.True(!paths.Except(ExpectedPaths).Any(),
            "in the spec but undeclared here: " + string.Join(", ", paths.Except(ExpectedPaths)));
    }

    [Fact]
    public async Task Every_operation_has_a_summary_and_a_success_schema()
    {
        using var host = new RunnerHost();
        var spec = await SpecAsync(host);

        var bare = new List<string>();
        foreach (var path in spec.GetProperty("paths").EnumerateObject())
        foreach (var op in path.Value.EnumerateObject())
        {
            var name = $"{op.Name.ToUpperInvariant()} {path.Name}";

            if (!op.Value.TryGetProperty("summary", out var summary)
                || string.IsNullOrWhiteSpace(summary.GetString()))
                bare.Add($"{name}: no summary");

            if (!op.Value.TryGetProperty("responses", out var responses))
            {
                bare.Add($"{name}: no responses");
                continue;
            }

            // A success response must describe a body — either a schema or an explicit binary
            // content type. "200 with no content declared" is the shape a forgotten route takes.
            var success = responses.EnumerateObject()
                .FirstOrDefault(r => r.Name.StartsWith('2'));
            if (success.Value.ValueKind == JsonValueKind.Undefined)
            {
                bare.Add($"{name}: no 2xx response");
                continue;
            }
            var is204 = success.Name == "204";
            if (!is204 && !success.Value.TryGetProperty("content", out _))
                bare.Add($"{name}: 2xx declares no content");
        }

        Assert.True(bare.Count == 0, string.Join("\n", bare));
    }

    [Fact]
    public async Task Error_statuses_are_declared_with_the_error_schema()
    {
        using var host = new RunnerHost();
        var spec = await SpecAsync(host);

        // The taxonomy is the API's most-used surface after the happy path. A route that can
        // 429 or 504 and does not say so sends a caller to read the source.
        var solve = spec.GetProperty("paths").GetProperty("/solve").GetProperty("post")
                        .GetProperty("responses");
        foreach (var status in new[] { "400", "404", "422", "429", "500", "504" })
            Assert.True(solve.TryGetProperty(status, out _), $"/solve does not declare {status}");

        // build-solve carries issues[] on BOTH its rejection depths — the API's structural pass
        // (400) and the engine's (422). Documenting only one sends a client looking in the wrong
        // place for the reason a document was refused.
        var build = spec.GetProperty("paths").GetProperty("/flowsheets/build-solve")
                        .GetProperty("post").GetProperty("responses");
        foreach (var status in new[] { "400", "422" })
        {
            var schemaRef = build.GetProperty(status).GetProperty("content")
                .GetProperty("application/json").GetProperty("schema")
                .GetProperty("$ref").GetString();
            Assert.Equal("#/components/schemas/DocumentErrorResponse", schemaRef);
        }
    }

    [Fact]
    public async Task Descriptions_reach_the_document_from_param_tags()
    {
        using var host = new RunnerHost();
        var spec = await SpecAsync(host);

        // The CS1587 trap: `///` inside a record's parameter list compiles and is DISCARDED.
        // If that regresses, the source still reads as documented and every field description
        // here goes blank — so assert a specific one actually arrived.
        var buildRef = spec.GetProperty("components").GetProperty("schemas")
            .GetProperty("HealthResponse").GetProperty("properties")
            .GetProperty("buildRef");

        Assert.True(buildRef.TryGetProperty("description", out var d),
            "HealthResponse.buildRef lost its description — are the <param> tags still intact?");
        Assert.Contains("runner build", d.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_key_scheme_is_declared()
    {
        using var host = new RunnerHost();
        var spec = await SpecAsync(host);

        var scheme = spec.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Docs_are_gated_like_every_other_route_by_default()
    {
        // An earlier draft exempted /openapi.json and /docs alongside /health, so the browser UI
        // could load. FND-0002 removed the "unset = open" configuration from this service and says
        // in writing that /health is the ONLY exemption; a second exemption list in the middleware
        // is the shape of the defect it fixed. So the docs are gated, and DOCS_PUBLIC is an
        // explicit opt-out whose FORGOTTEN state is closed.
        using var host = new RunnerHost();
        using var anonymous = host.Factory.CreateClient();   // no X-Api-Key

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/docs/index.html")).StatusCode);

        // /health remains the one route a key is never needed for.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);

        // ...and a keyed caller still gets them, which is what makes client generation possible.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/openapi.json")).StatusCode);
    }

    [Fact]
    public async Task Docs_public_opens_them_and_nothing_else()
    {
        using var host = new RunnerHost(new() { ["DOCS_PUBLIC"] = "true" });
        using var anonymous = host.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/docs/index.html")).StatusCode);

        // The flag widens the docs and NOTHING else — the gate still bites on real routes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/templates")).StatusCode);
    }

    [Fact]
    public async Task Unkeyed_runner_refuses_the_docs_too()
    {
        // The 503 refusal reaches the docs as well: with no server key there is no configuration
        // in which anything but /health answers.
        using var host = new RunnerHost(new() { ["RUNNER_API_KEY"] = "" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health")).StatusCode);
    }

    // ── the schema cannot lie about a response ────────────────────────────

    [Theory]
    [InlineData("/solve")]
    [InlineData("/flowsheets/build-solve")]
    public async Task Declared_response_shapes_name_every_field_the_worker_sends(string route)
    {
        using var host = new RunnerHost();
        host.AddTemplate("demo");

        var resp = route == "/solve"
            ? await host.Client.PostAsJsonAsync(route, new { templateId = "demo" })
            : await host.Client.PostAsJsonAsync(route, new { document = JsonSerializer.Deserialize<JsonElement>(ValidDoc) });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var actual = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Round-trip the real body through the DECLARED record. Response bodies are worker
        // pass-through, so nothing at runtime would ever notice a field the record omits —
        // this is the only place that can.
        var declared = route == "/solve"
            ? JsonSerializer.SerializeToElement(
                JsonSerializer.Deserialize<SolveResponse>(actual, Json), Json)
            : JsonSerializer.SerializeToElement(
                JsonSerializer.Deserialize<BuildSolveResponse>(actual, Json), Json);

        var dropped = MissingPaths(actual, declared).ToList();
        Assert.True(dropped.Count == 0,
            $"{route}: the worker sends fields the response record does not name: {string.Join(", ", dropped)}");
    }

    /// Field paths present in <paramref name="sent"/> and absent from <paramref name="declared"/>.
    /// Recurses objects and the FIRST element of an array — enough to catch a dropped row field
    /// without asserting anything about how many rows a fixture happens to have.
    private static IEnumerable<string> MissingPaths(JsonElement sent, JsonElement declared, string prefix = "")
    {
        if (sent.ValueKind == JsonValueKind.Object)
        {
            if (declared.ValueKind != JsonValueKind.Object) { yield return prefix; yield break; }
            foreach (var prop in sent.EnumerateObject())
            {
                var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                if (!declared.TryGetProperty(prop.Name, out var mirror)) { yield return path; continue; }
                foreach (var m in MissingPaths(prop.Value, mirror, path)) yield return m;
            }
        }
        else if (sent.ValueKind == JsonValueKind.Array && sent.GetArrayLength() > 0)
        {
            if (declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() == 0) yield break;
            foreach (var m in MissingPaths(sent[0], declared[0], $"{prefix}[0]")) yield return m;
        }
    }

    /// The same separator flowsheet the build-solve tests use — it passes structural validation
    /// and the FakeWorker returns a fully populated result for it, which is what makes it a
    /// useful specimen here: a thin document would exercise fewer response fields.
    private const string ValidDoc = """
    {
      "schemaVersion": 1,
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 0, "unit": "C" },
                    "pressure": { "value": 50, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methane": 0.5, "Ethane": 0.5 } } } },
        { "tag": "V-1", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "V-1", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;
}
