// Spec 145 — the worker's stdout carries exactly one JSON document, even when a
// native library prints to the descriptor underneath it.
//
// Measured 2026-08-08. `Console.SetOut(Console.Error)` in Program.cs was written to
// hold that contract, and it holds it for MANAGED writes only. Ipopt — loaded by
// NRTL's flash on a reacting mixture — writes its EPL banner straight to fd 1:
//
//   ******************************************************************************
//   This program contains Ipopt, a library for large-scale nonlinear optimization.
//   ...
//   {"converged":true,"elapsedMs":2982,"streams":[...
//
// The solve had SUCCEEDED. The API could not parse the reply and reported
// `WORKER_CRASH: worker returned an invalid response`; 13 of the platform eval
// corpus's NRTL cases scored 0 for a banner.
//
// This test goes through the API, which is where the corruption was observed — a
// test that only asserted "the solve converges" would have passed throughout,
// because it always did.

using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DwsimRunner.Integration.Tests;

[Trait("Category", "Protocol")]
public class ProtocolChannelTests
{
    // Methanol dehydration on NRTL — the smallest document that loads Ipopt.
    // NRTL is load-bearing: the identical document under PR or SRK never reaches
    // the solver and so never printed the banner, which is exactly why this read
    // as a property-package limitation for as long as it did.
    private const string NrtlReactionDoc = """
    {
      "schemaVersion": 1,
      "name": "protocol channel — NRTL loads Ipopt",
      "compounds": ["Methanol", "Dimethyl ether", "Water"],
      "propertyPackage": "NRTL",
      "objects": [
        { "tag": "FEED", "kind": "materialStream",
          "spec": { "temperature": { "value": 250, "unit": "C" },
                    "pressure": { "value": 15, "unit": "bar" },
                    "molarFlow": { "value": 100, "unit": "kmol/h" },
                    "composition": { "basis": "molar", "fractions": { "Methanol": 1 } } } },
        { "tag": "R-1", "kind": "unitOp", "type": "reactorConversion",
          "parameters": { "outletTemperature": { "value": 250, "unit": "C" } } },
        { "tag": "OUT_V", "kind": "materialStream" },
        { "tag": "OUT_L", "kind": "materialStream" },
        { "tag": "Q-RX", "kind": "energyStream" }
      ],
      "connections": [
        { "from": "FEED", "to": "R-1", "port": "Inlet" },
        { "from": "R-1", "to": "OUT_V", "port": "Vapor Outlet" },
        { "from": "R-1", "to": "OUT_L", "port": "Liquid Outlet" },
        { "from": "Q-RX", "to": "R-1", "port": "Energy Inlet" }
      ],
      "reactions": [
        { "tag": "RX-1", "type": "conversion", "basis": "molar",
          "stoichiometry": { "Methanol": -2, "Dimethyl ether": 1, "Water": 1 },
          "baseCompound": "Methanol", "conversionExpression": "80" }
      ],
      "reactionSets": [
        { "tag": "RS-1", "reactions": ["RX-1"], "attachTo": ["R-1"] }
      ]
    }
    """;

    private static StringContent Body(string doc) =>
        new($"{{\"document\":{doc}}}", Encoding.UTF8, "application/json");

    // On the pre-fix worker this FAILS with WORKER_CRASH / "worker returned an
    // invalid response" — the banner sits ahead of the payload on fd 1.
    [SkippableFact]
    public async Task Native_library_output_does_not_corrupt_the_worker_reply()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", Body(NrtlReactionDoc));
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        // The distinguishing assertion. A crashed worker also answers 200, so status
        // alone proves nothing — the reply must be the RESULT, not an error envelope.
        Assert.False(body.TryGetProperty("error", out var err),
            $"worker reply carried an error: {(err.ValueKind == JsonValueKind.Undefined ? "" : err.ToString())} — raw: {Trim(raw)}");

        Assert.True(body.GetProperty("converged").GetBoolean());
        Assert.True(body.GetProperty("streams").GetArrayLength() > 0);
    }

    // A ratchet against fixing this at the wrong layer: if someone ever makes the
    // API tolerate noise by scanning for "the last JSON object on stdout", the
    // banner starts reaching the client and this catches it.
    //
    // HONEST LIMIT, so nobody reads more into a green run than is there: this test
    // passes on the BROKEN worker too. The API answers a corrupted reply with its
    // own clean error envelope, so the banner never crosses the wire either way,
    // and from out here the two are indistinguishable. Only the test above is
    // discriminating (verified: it fails against a pre-fix runner). Proving the
    // worker's raw stdout is clean means reading the worker's stdout, which needs
    // a harness that spawns it directly — deliberately not built for one assertion.
    [SkippableFact]
    public async Task The_reply_is_json_and_nothing_else()
    {
        Skip.IfNot(RunnerConnection.Available, RunnerConnection.SkipReason);

        var resp = await RunnerConnection.Client.PostAsync("/flowsheets/build-solve", Body(NrtlReactionDoc));
        var raw = (await resp.Content.ReadAsStringAsync()).TrimStart();

        Assert.StartsWith("{", raw);
        Assert.DoesNotContain("Ipopt", raw);
        Assert.DoesNotContain("****", raw);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
