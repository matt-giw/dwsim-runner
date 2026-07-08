// T028 — US1 Tier B: build-and-solve a flash-drum document against a running
// runner with real DWSIM. Proves the document → engine construction path end
// to end: convergence, stream harvesting, and mass-balance closure
// (quickstart Scenario 2).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "BuildSolve")]
public class BuildSolveTests
{
    internal const string FlashDrumDoc = """
    {
      "schemaVersion": 1,
      "name": "flash drum integration",
      "compounds": ["Methane", "Ethane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": -40, "unit": "C" },
                    "pressure": { "value": 10, "unit": "bar" },
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

    internal static StringContent BuildSolveBody(string doc, int timeoutSeconds = 120)
    {
        var payload = new Dictionary<string, object?>
        {
            ["document"] = JsonSerializer.Deserialize<JsonElement>(doc),
            ["timeoutSeconds"] = timeoutSeconds,
        };
        return new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
    }

    internal static double MassFlow(JsonElement result, string name) =>
        result.GetProperty("streams").EnumerateArray()
              .First(s => s.GetProperty("name").GetString() == name)
              .GetProperty("massFlowKgH").GetDouble();

    [SkippableFact]
    public async Task Flash_drum_document_builds_solves_and_closes_the_mass_balance()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(FlashDrumDoc));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        Assert.True(r.GetProperty("converged").GetBoolean(),
            "flash drum did not converge: " + r.GetProperty("warnings"));

        Assert.Equal(4, r.GetProperty("build").GetProperty("objectsCreated").GetInt32());
        Assert.Equal(3, r.GetProperty("build").GetProperty("connectionsMade").GetInt32());

        var feed = MassFlow(r, "FEED");
        var vap = MassFlow(r, "VAP");
        var liq = MassFlow(r, "LIQ");
        Assert.InRange(feed, 99.0, 101.0);
        Assert.InRange((vap + liq) / feed, 0.99, 1.01);   // SC: mass balance closes > 99%
    }

    [SkippableFact]
    public async Task Identical_documents_are_served_from_cache_byte_identically()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var r1 = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(FlashDrumDoc));
        var r2 = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", BuildSolveBody(FlashDrumDoc));

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(await r1.Content.ReadAsStringAsync(), await r2.Content.ReadAsStringAsync());
    }
}
