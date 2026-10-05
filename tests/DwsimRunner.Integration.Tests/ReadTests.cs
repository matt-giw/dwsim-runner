// iskra spec 285 (ISK-485) — Tier B: POST /flowsheets/read against the real engine.
//
// SC-001's file half, closed in both directions: a document is built, solved and SAVED by the
// engine, the saved file is read back with DWSIM's own loader, and the document that comes back
// is (1) the same flowsheet — compounds, package, units, wiring, feed and unit specifications —
// and (2) solvable again to the same answer. The accounting invariant (every simulation object in
// exactly one bucket) and GP-3 (no stored result in the document) are asserted on the way.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Read")]
public class ReadTests
{
    private const string HeaterFlashDoc = """
    {
      "schemaVersion": 1,
      "name": "read round trip",
      "compounds": ["Methane", "Ethane", "Propane"],
      "propertyPackage": "PR",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": -60, "unit": "C" },
                    "pressure": { "value": 10, "unit": "bar" },
                    "massFlow": { "value": 100, "unit": "kg/h" },
                    "composition": { "basis": "molar",
                                     "fractions": { "Methane": 0.4, "Ethane": 0.3, "Propane": 0.3 } } } },
        { "tag": "H-1", "kind": "unitOp", "type": "heater",
          "parameters": { "outletTemperature": { "value": -30, "unit": "C" },
                          "pressureDrop": { "value": 0.2, "unit": "bar" } } },
        { "tag": "S2", "kind": "materialStream" },
        { "tag": "V-1", "kind": "unitOp", "type": "separator" },
        { "tag": "VAP", "kind": "materialStream" },
        { "tag": "LIQ", "kind": "materialStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "H-1", "port": "Inlet" },
        { "from": "H-1", "to": "S2", "port": "Outlet" },
        { "from": "S2", "to": "V-1", "port": "Inlet" },
        { "from": "V-1", "to": "VAP", "port": "Vapor Outlet" },
        { "from": "V-1", "to": "LIQ", "port": "Liquid Outlet" }
      ]
    }
    """;

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Read(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await RunnerConnection.Client.PostAsync("/flowsheets/read", content);
    }

    private static double VaporKgH(JsonElement result) =>
        result.GetProperty("streams").EnumerateArray()
              .First(s => s.GetProperty("name").GetString() == "VAP").GetProperty("massFlowKgH").GetDouble();

    [SkippableFact]
    public async Task A_runner_saved_file_reads_back_as_the_same_solvable_document()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        const string id = "it-read-roundtrip";
        var source = JsonSerializer.Deserialize<JsonElement>(HeaterFlashDoc);

        var save = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", Json(new
        {
            document = source, timeoutSeconds = 180, saveAsTemplate = new { id, overwrite = true },
        }));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var original = JsonSerializer.Deserialize<JsonElement>(await save.Content.ReadAsStringAsync());
        try
        {
            var bytes = await RunnerConnection.Client.GetByteArrayAsync($"/templates/{id}/file");
            var resp = await Read(bytes);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var read = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var doc = read.GetProperty("document");

            Assert.Equal(["Ethane", "Methane", "Propane"],
                doc.GetProperty("compounds").EnumerateArray().Select(c => c.GetString()!).Order());
            Assert.Equal("PR", doc.GetProperty("propertyPackage").GetString());
            // Measured: the headless SaveFlowsheet2 writes a GeneralInfo with no BuildVersion, so a
            // runner-saved file states no version — null, never a guess. (FOSSEE files state theirs.)
            Assert.Equal(JsonValueKind.Null, read.GetProperty("savedBy").ValueKind);

            var objects = doc.GetProperty("objects").EnumerateArray().ToDictionary(o => o.GetProperty("tag").GetString()!);
            Assert.Equal("heater", objects["H-1"].GetProperty("type").GetString());
            Assert.Equal("separator", objects["V-1"].GetProperty("type").GetString());

            var wiring = doc.GetProperty("connections").EnumerateArray()
                .Select(c => $"{c.GetProperty("from").GetString()}>{c.GetProperty("to").GetString()}@{c.GetProperty("port").GetString()}")
                .Order().ToList();
            var expectedWiring = source.GetProperty("connections").EnumerateArray()
                .Select(c => $"{c.GetProperty("from").GetString()}>{c.GetProperty("to").GetString()}@{c.GetProperty("port").GetString()}")
                .Order().ToList();
            Assert.Equal(expectedWiring, wiring);

            // Feed: T -60 C, P 10 bar, 100 kg/h, in SI.
            var feed = objects["FEED"].GetProperty("spec");
            Assert.Equal(213.15, feed.GetProperty("temperature").GetProperty("value").GetDouble(), 3);
            Assert.Equal(1_000_000, feed.GetProperty("pressure").GetProperty("value").GetDouble(), 1);
            Assert.Equal(100.0 / 3600, feed.GetProperty("massFlow").GetProperty("value").GetDouble(), 6);
            Assert.Equal(0.4, feed.GetProperty("composition").GetProperty("fractions").GetProperty("Methane").GetDouble(), 6);

            // The heater: the mode the builder inferred, the setpoint it reads, and NOT its computed duty.
            var heater = objects["H-1"].GetProperty("parameters");
            Assert.Equal("outletTemperature", heater.GetProperty("calcMode").GetString());
            Assert.Equal(243.15, heater.GetProperty("outletTemperature").GetProperty("value").GetDouble(), 3);
            Assert.Equal(20_000, heater.GetProperty("pressureDrop").GetProperty("value").GetDouble(), 1);
            Assert.False(heater.TryGetProperty("heatDuty", out _), "the solved duty came back as a specification");

            // GP-3: only feeds carry a spec; results are in `stored`.
            foreach (var tag in new[] { "S2", "VAP", "LIQ" })
                Assert.False(objects[tag].TryGetProperty("spec", out _), $"{tag} is an outlet and carries a spec");
            Assert.True(read.GetProperty("stored").GetProperty("solved").GetBoolean());
            Assert.Equal(VaporKgH(original) / 3600,
                read.GetProperty("stored").GetProperty("streams").GetProperty("VAP").GetProperty("massFlow_kg_s").GetDouble(), 4);

            // Accounting: every object once — here, all in the document.
            var placeholders = read.GetProperty("placeholders").EnumerateArray().Select(p => p.GetProperty("tag").GetString()!).ToList();
            var all = objects.Keys.Concat(placeholders).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.Equal(read.GetProperty("layout").EnumerateObject().Select(p => p.Name).Order(), all.Order());
            Assert.Empty(placeholders);

            // ...and the document that came back solves to the same answer.
            var again = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", Json(new { document = doc, timeoutSeconds = 180 }));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var resolved = JsonSerializer.Deserialize<JsonElement>(await again.Content.ReadAsStringAsync());
            Assert.True(resolved.GetProperty("converged").GetBoolean());
            Assert.Equal(VaporKgH(original), VaporKgH(resolved), 2);
        }
        finally
        {
            await RunnerConnection.Client.DeleteAsync($"/templates/{id}");
        }
    }

    [SkippableFact]
    public async Task A_file_that_is_not_dwsim_is_refused_without_the_engine()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await Read("not a flowsheet"u8.ToArray());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    // A loader failure is logged by DWSIM.Logging.Logger, whose type initializer creates
    // "<DWSIM_PATH>/DWSIM Application Data". As the non-root runner user that threw, and the 422 then
    // named the LOGGER for every file that failed to load — the real cause (a missing property-package
    // library, measured on 14 FOSSEE files) never reached the caller.
    [SkippableFact]
    public async Task A_load_failure_names_the_engine_cause_not_the_logger()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("x.xml").Open()))
            w.Write("<?xml version=\"1.0\"?><DWSIM_Simulation_Data><GeneralInfo><Bad>");

        var resp = await Read(buffer.ToArray());
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("LOAD_FAILED", body.GetProperty("error").GetString());
        Assert.DoesNotContain("DWSIM.Logging.Logger", body.GetProperty("message").GetString());
    }
}
