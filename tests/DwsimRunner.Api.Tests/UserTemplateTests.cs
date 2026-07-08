// T035 — US3: user-template persistence over the fake worker. Covers
// build-solve saveAsTemplate (saved flag, 409 TEMPLATE_NAME_CONFLICT,
// overwrite:true, provenance sidecar), DELETE /templates/{id}
// (204 user / 403 curated / 404 unknown), and the object-shaped
// GET /templates listing (contracts/runner-api-v2.md).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class UserTemplateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    private static StringContent SaveBody(string doc, string id, bool? overwrite = null)
    {
        var save = new Dictionary<string, object?> { ["id"] = id };
        if (overwrite is not null) save["overwrite"] = overwrite;
        var payload = new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(doc),
            ["saveAsTemplate"] = save,
        };
        return new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Save_as_template_persists_and_reports_the_template_block()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "flash-drum-demo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        var template = body.GetProperty("template");
        Assert.Equal("flash-drum-demo", template.GetProperty("id").GetString());
        Assert.Equal("user", template.GetProperty("source").GetString());
        Assert.True(template.GetProperty("saved").GetBoolean());

        Assert.True(File.Exists(Path.Combine(host.UserTemplatesDir, "flash-drum-demo.dwxmz")));
        Assert.True(File.Exists(Path.Combine(host.UserTemplatesDir, "flash-drum-demo.doc.json")),
            "provenance sidecar <id>.doc.json must be written alongside the .dwxmz");
    }

    [Fact]
    public async Task Unsolved_save_persists_with_solvedAtSave_false()
    {
        var notConvergedDoc = ValidDoc
            .Replace("\"tag\": \"V-1\"", "\"tag\": \"__not-converged\"")
            .Replace("\"to\": \"V-1\"", "\"to\": \"__not-converged\"")
            .Replace("\"from\": \"V-1\"", "\"from\": \"__not-converged\"");
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(notConvergedDoc, "unsolved-demo"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("converged").GetBoolean());
        Assert.True(File.Exists(Path.Combine(host.UserTemplatesDir, "unsolved-demo.dwxmz")));

        var listing = await host.Client.GetFromJsonAsync<JsonElement>("/templates", Json);
        var entry = listing.EnumerateArray().First(t => t.GetProperty("id").GetString() == "unsolved-demo");
        Assert.False(entry.GetProperty("solvedAtSave").GetBoolean());
    }

    [Fact]
    public async Task Existing_name_without_overwrite_is_409_conflict()
    {
        using var host = new RunnerHost();
        (await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "dup-name"))).EnsureSuccessStatusCode();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "dup-name"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("TEMPLATE_NAME_CONFLICT", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Curated_name_conflicts_too()
    {
        using var host = new RunnerHost();
        host.AddTemplate("methanol_synthesis");   // curated

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "methanol_synthesis"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Overwrite_true_replaces_the_existing_user_template()
    {
        using var host = new RunnerHost();
        (await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "ow-demo"))).EnsureSuccessStatusCode();

        var resp = await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "ow-demo", overwrite: true));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ow-demo", body.GetProperty("template").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Listing_is_object_shaped_with_source_field()
    {
        using var host = new RunnerHost();
        host.AddTemplate("methanol_synthesis");
        (await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "flash-drum-demo"))).EnsureSuccessStatusCode();

        var listing = await host.Client.GetFromJsonAsync<JsonElement>("/templates", Json);

        var entries = listing.EnumerateArray().ToList();
        var curated = entries.First(t => t.GetProperty("id").GetString() == "methanol_synthesis");
        Assert.Equal("curated", curated.GetProperty("source").GetString());

        var user = entries.First(t => t.GetProperty("id").GetString() == "flash-drum-demo");
        Assert.Equal("user", user.GetProperty("source").GetString());
        Assert.True(user.TryGetProperty("createdUtc", out var created) && created.ValueKind == JsonValueKind.String);
        Assert.True(user.GetProperty("solvedAtSave").GetBoolean());
    }

    [Fact]
    public async Task Health_templates_field_stays_a_bare_id_array()
    {
        using var host = new RunnerHost();
        host.AddTemplate("methanol_synthesis");

        var health = await host.Client.GetFromJsonAsync<JsonElement>("/health", Json);

        Assert.All(health.GetProperty("templates").EnumerateArray(),
            t => Assert.Equal(JsonValueKind.String, t.ValueKind));
    }

    [Fact]
    public async Task Saved_template_is_solvable_via_the_spec001_pipeline()
    {
        using var host = new RunnerHost();
        (await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "resolvable"))).EnsureSuccessStatusCode();

        var solve = await host.Client.PostAsJsonAsync("/solve", new { templateId = "resolvable" });

        Assert.Equal(HttpStatusCode.OK, solve.StatusCode);
        var body = await solve.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.GetProperty("converged").GetBoolean());
    }

    [Fact]
    public async Task Delete_user_template_returns_204_and_it_disappears()
    {
        using var host = new RunnerHost();
        (await host.Client.PostAsync("/flowsheets/build-solve", SaveBody(ValidDoc, "deletable"))).EnsureSuccessStatusCode();

        var del = await host.Client.DeleteAsync("/templates/deletable");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.UserTemplatesDir, "deletable.dwxmz")));

        var solve = await host.Client.PostAsJsonAsync("/solve", new { templateId = "deletable" });
        Assert.Equal(HttpStatusCode.NotFound, solve.StatusCode);
    }

    [Fact]
    public async Task Delete_curated_template_is_403_readonly()
    {
        using var host = new RunnerHost();
        host.AddTemplate("methanol_synthesis");

        var del = await host.Client.DeleteAsync("/templates/methanol_synthesis");

        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        var body = await del.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("TEMPLATE_READONLY", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_unknown_template_is_404()
    {
        using var host = new RunnerHost();
        var del = await host.Client.DeleteAsync("/templates/never-existed");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}
