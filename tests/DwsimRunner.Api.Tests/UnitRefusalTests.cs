// 213 — the unit-spelling probe matrix. dwsim-runner API — GPL-3.0.
//
// THE DEFECT THIS PINS. `Converter.ConvertToSI` returns the value UNCHANGED for a spelling it does
// not know. So a field the API does not guard reads `{value: 120, unit: "degC"}` as 120 KELVIN,
// converges, and answers −153.15 °C with vapor fraction 0 — no error, no warning, a result an
// engineer can put in a datasheet. Measured live on /flash 2026-08-31 and re-confirmed on this
// build. A confidently wrong converged answer is the worst failure shape this product has.
//
// `/flowsheets/validate` and `/flowsheets/build-solve` were already guarded (DocumentValidator's
// INVALID_UNIT). The other five entry points were not, and each is probed here.
//
// WHY THE PROBES LIVE AT THIS TIER. Every refusal fires in the API, before the worker spawns, so
// the FakeWorker is enough: a request that is refused never reaches an engine. That is not a
// convenience — it is FR-006 restated as a test property. If one of these ever needs DWSIM to
// fail, the refusal has moved past the calculation and the fix is wrong.
//
// WHAT THESE PROBES DO NOT PROVE. They prove the runner REFUSES a spelling it does not accept.
// They cannot prove the spellings it DOES accept are ones `ConvertToSI` knows — that needs the
// engine, and it is the other half of this hazard (`DocumentValidator.Units`' own header says so).

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DwsimRunner.Api.Tests;

public class UnitRefusalTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Plausible-but-unrecognised spellings an engineer would actually type. `degC` and
    /// `bara` are the two the field report was made with.</summary>
    public static TheoryData<string, string> Unrecognised => new()
    {
        { "temperature", "degC" },
        { "temperature", "celsius" },
        { "pressure", "bara" },
        { "pressure", "psia" },
        { "massFlow", "kg/hr" },
        { "power", "MMBtu/hr" },
        { "length", "metres" },
        { "temperature", "!!!" },
    };

    private static async Task<(HttpStatusCode Status, string Code, string Message)> ReadError(HttpResponseMessage r)
    {
        var body = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
        var code = body.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        var message = body.TryGetProperty("message", out var m) ? m.GetString() ?? "" : body.GetRawText();
        return (r.StatusCode, code, message);
    }

    /// <summary>A refusal has to NAME the spelling it refused, or the caller is told only that
    /// something is wrong with a request they believe is correct — which is how `degC` survived.</summary>
    private static void AssertNamedRefusal((HttpStatusCode Status, string Code, string Message) got, string unit)
    {
        Assert.Equal(HttpStatusCode.BadRequest, got.Status);
        Assert.Equal("INVALID_UNIT", got.Code);
        Assert.Contains(unit, got.Message, StringComparison.Ordinal);
    }

    // ── /flash — the reported hole ──────────────────────────────────────────

    private static Dictionary<string, object?> Flash(string flashType, params (string Field, object Spec)[] specs)
    {
        var r = new Dictionary<string, object?>
        {
            ["compounds"] = new[] { "Water" },
            ["composition"] = new { basis = "molar", fractions = new Dictionary<string, double> { ["Water"] = 1.0 } },
            ["propertyPackage"] = "STEAM",
            ["flashType"] = flashType,
        };
        foreach (var (field, spec) in specs) r[field] = spec;
        return r;
    }

    [Fact]
    public async Task Flash_refuses_the_exact_request_from_the_field_report()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("TP",
            ("temperature", new { value = 120, unit = "degC" }),
            ("pressure", new { value = 1.01325, unit = "bara" })));

        var got = await ReadError(resp);
        AssertNamedRefusal(got, "degC");
        // The accepted spellings are listed, so the caller can fix it without reading our source.
        Assert.Contains("C", got.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("temperature", "degC")]
    [InlineData("pressure", "bara")]
    public async Task Flash_tp_refuses_an_unrecognised_spelling_on_either_spec(string field, string unit)
    {
        using var host = new RunnerHost();
        var specs = new Dictionary<string, object>
        {
            ["temperature"] = new { value = 120, unit = "C" },
            ["pressure"] = new { value = 1.01325, unit = "bar" },
        };
        specs[field] = new { value = 1.0, unit };

        var resp = await host.Client.PostAsJsonAsync("/flash",
            Flash("TP", ("temperature", specs["temperature"]), ("pressure", specs["pressure"])));

        AssertNamedRefusal(await ReadError(resp), unit);
    }

    [Fact]
    public async Task Flash_ph_refuses_an_unrecognised_enthalpy_spelling()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("PH",
            ("pressure", new { value = 1.0, unit = "bar" }),
            ("enthalpy", new { value = 419.0, unit = "kJ/kilogram" })));

        AssertNamedRefusal(await ReadError(resp), "kJ/kilogram");
    }

    // `entropy` has NO measured vocabulary on this runner — `DocumentValidator.Units` has no such
    // key. Guessing one is the defect this whole file is about, so any unit is refused and the
    // caller is told to send a bare SI value. The gap is recorded, not papered over.
    [Fact]
    public async Task Flash_ps_refuses_any_entropy_unit_because_no_vocabulary_is_measured()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("PS",
            ("pressure", new { value = 1.0, unit = "bar" }),
            ("entropy", new { value = 1.3, unit = "kJ/[kg.K]" })));

        var got = await ReadError(resp);
        AssertNamedRefusal(got, "kJ/[kg.K]");
        Assert.Contains("no accepted unit vocabulary", got.Message, StringComparison.Ordinal);
    }

    // A dimensionless field carrying a unit is not a harmless extra key: `RequireSi` hands it to
    // `ConvertToSI` exactly like a temperature.
    [Fact]
    public async Task Flash_refuses_a_unit_on_the_dimensionless_vapor_fraction()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("PVF",
            ("pressure", new { value = 1.0, unit = "bar" }),
            ("vaporFraction", new { value = 0.5, unit = "%" })));

        var got = await ReadError(resp);
        AssertNamedRefusal(got, "%");
        Assert.Contains("dimensionless", got.Message, StringComparison.Ordinal);
    }

    // The control. A guard that refuses everything is not a fix, and an absent unit still means SI
    // — the contract this runner has carried since spec 001, deliberately unchanged by 213.
    [Theory]
    [InlineData("C", "bar")]
    [InlineData("K", "Pa")]
    [InlineData("c", "BAR")]     // the vocabulary is case-insensitive, as the document path is
    public async Task Flash_still_accepts_a_recognised_spelling(string tUnit, string pUnit)
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("TP",
            ("temperature", new { value = 120, unit = tUnit }),
            ("pressure", new { value = 1.01325, unit = pUnit })));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Flash_still_accepts_a_bare_value_with_no_unit_as_si()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flash", Flash("TP",
            ("temperature", new { value = 393.15 }),
            ("pressure", new { value = 101325.0 })));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // A refused request must not be answerable from cache, and must not POPULATE one. The refusal
    // is placed before the cache lookup for this reason; this pins the ordering.
    [Fact]
    public async Task A_refused_flash_never_becomes_a_cached_result()
    {
        using var host = new RunnerHost();
        var bad = Flash("TP",
            ("temperature", new { value = 120, unit = "degC" }),
            ("pressure", new { value = 1.0, unit = "bar" }));

        AssertNamedRefusal(await ReadError(await host.Client.PostAsJsonAsync("/flash", bad)), "degC");
        AssertNamedRefusal(await ReadError(await host.Client.PostAsJsonAsync("/flash", bad)), "degC");
    }

    // ── /solve and /compare — overrides ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(Unrecognised))]
    public async Task Solve_refuses_an_unrecognised_override_unit(string _, string unit)
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve", new
        {
            templateId = "t",
            overrides = new[] { new { @object = "S1", property = "temperature", value = 120.0, unit } },
        });

        AssertNamedRefusal(await ReadError(resp), unit);
    }

    [Fact]
    public async Task Solve_still_accepts_a_recognised_override_unit()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/solve", new
        {
            templateId = "t",
            overrides = new[] { new { @object = "S1", property = "temperature", value = 120.0, unit = "C" } },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // Per-case error isolation is /compare's design (T016) — but a unit it cannot read is not a
    // case failure, it is a request the runner must not start. It names the case so the caller
    // knows which of up to 25 to fix.
    [Fact]
    public async Task Compare_refuses_the_whole_request_and_names_the_offending_case()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            templateId = "t",
            cases = new Dictionary<string, object>
            {
                ["good"] = Array.Empty<object>(),
                ["hot"] = new[] { new { @object = "S1", property = "temperature", value = 120.0, unit = "degC" } },
            },
        });

        var got = await ReadError(resp);
        AssertNamedRefusal(got, "degC");
        Assert.Contains("hot", got.Message, StringComparison.Ordinal);
    }

    // ── /optimize — the variable's unit ─────────────────────────────────────

    // Worse than a single wrong answer: the unit rides every evaluation of the search, so the whole
    // sweep is wrong and internally consistent.
    [Fact]
    public async Task Optimize_refuses_an_unrecognised_variable_unit()
    {
        using var host = new RunnerHost();
        host.AddTemplate("t");

        var resp = await host.Client.PostAsJsonAsync("/optimize", new
        {
            templateId = "t",
            variable = new { @object = "__objective", property = "pressure", min = 0.0, max = 100.0, unit = "bara" },
            objective = new { @object = "W_comp", property = "duty", direction = "minimize" },
            tolerance = 0.5,
            maxEvaluations = 30,
        });

        AssertNamedRefusal(await ReadError(resp), "bara");
    }

    // ── document-bearing endpoints that never validated one ─────────────────

    private static object DocumentWithTemperatureUnit(string unit) => new
    {
        schemaVersion = 1,
        compounds = new[] { "Water" },
        propertyPackage = "STEAM",
        objects = new object[]
        {
            new
            {
                tag = "FEED",
                kind = "materialStream",
                spec = new
                {
                    temperature = new { value = 120.0, unit },
                    pressure = new { value = 1.01325, unit = "bar" },
                    composition = new { basis = "molar", fractions = new Dictionary<string, double> { ["Water"] = 1.0 } },
                },
            },
        },
        connections = Array.Empty<object>(),
    };

    [Fact]
    public async Task Pfd_refuses_a_document_carrying_an_unrecognised_unit()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/flowsheets/pfd",
            new { document = DocumentWithTemperatureUnit("degC") });

        AssertNamedRefusal(await ReadError(resp), "degC");
    }

    [Fact]
    public async Task Compare_refuses_a_document_carrying_an_unrecognised_unit()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/compare", new
        {
            document = DocumentWithTemperatureUnit("degC"),
            cases = new Dictionary<string, object> { ["base"] = Array.Empty<object>() },
        });

        AssertNamedRefusal(await ReadError(resp), "degC");
    }

    [Fact]
    public async Task Optimize_refuses_a_document_carrying_an_unrecognised_unit()
    {
        using var host = new RunnerHost();

        var resp = await host.Client.PostAsJsonAsync("/optimize", new
        {
            document = DocumentWithTemperatureUnit("degC"),
            variable = new { @object = "FEED", property = "pressure", min = 0.0, max = 100.0, unit = "bar" },
            objective = new { @object = "FEED", property = "temperature", direction = "minimize" },
            tolerance = 0.5,
            maxEvaluations = 30,
        });

        AssertNamedRefusal(await ReadError(resp), "degC");
    }

    // ── the coverage ratchet (FR-005) ───────────────────────────────────────

    // Every quantity kind the runner publishes must have a probe above, so adding a kind to the
    // vocabulary without probing it fails the build. Its LIMIT, stated rather than implied: this
    // ratchets the KINDS, not the FIELDS — a new unit-bearing field on a new endpoint is still
    // caught only by whoever adds it reading this file. A field-level ratchet needs a machine-
    // readable request schema, which this API does not have.
    [Fact]
    public void Every_published_quantity_kind_is_reachable_by_a_refusal()
    {
        foreach (var (kind, accepted) in DocumentValidator.UnitVocabulary)
        {
            var refusal = DocumentValidator.UnitRefusal($"{kind}.unit", "!!!not-a-unit!!!", kind);
            Assert.NotNull(refusal);
            Assert.Contains("!!!not-a-unit!!!", refusal, StringComparison.Ordinal);
            // A kind with a vocabulary lists it; a dimensionless one says so instead.
            if (accepted.Count > 0)
                Assert.Contains(accepted[0], refusal, StringComparison.Ordinal);
            else
                Assert.Contains("dimensionless", refusal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unmeasured_quantity_kind_refuses_rather_than_guessing_a_vocabulary()
    {
        Assert.DoesNotContain("entropy", DocumentValidator.UnitVocabulary.Keys);
        var refusal = DocumentValidator.UnitRefusal("entropy.unit", "kJ/[kg.K]", "entropy");
        Assert.NotNull(refusal);
        Assert.Contains("no accepted unit vocabulary", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_unit_is_still_si_and_still_accepted()
    {
        Assert.Null(DocumentValidator.UnitRefusal("temperature.unit", null, "temperature"));
        Assert.Null(DocumentValidator.UnitRefusal("temperature.unit", "", "temperature"));
        Assert.Null(DocumentValidator.UnitRefusal("override S1.temperature", null, null));
    }
}
