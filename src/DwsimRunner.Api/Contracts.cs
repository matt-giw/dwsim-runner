// dwsim-runner API — GPL-3.0
//
// The wire contract, as types. These exist so the OpenAPI document generated at
// /openapi.json is a description of THIS service rather than a hand-written
// second opinion about it (ISK-231).
//
// Two kinds of type live here and they are not used the same way:
//
//   REQUEST types are BOUND. The framework deserializes into them and the
//   handler reads fields off the result. They replaced hand-rolled
//   JsonDocument parsing, so they are load-bearing at runtime.
//
//   RESPONSE types are DECLARED. Every solve-shaped body is produced by the
//   worker process and passed through as bytes — deliberately, because the API
//   must not need to know the result schema. If the API deserialized into these
//   records and re-serialized, any field the worker LEARNED to emit that these
//   records do not name would be silently dropped on the way out, and the two
//   processes would have to be upgraded in lockstep. (`solvingMethod` from spec
//   143 and the per-phase blocks from 120 both arrived that way.)
//
//   The cost of declaring rather than binding is that a response record can
//   drift from what the worker actually sends. That is not left to review:
//   tests/OpenApiContractTests.cs round-trips real worker output through these
//   records and fails on any field the schema does not name.
//
// Descriptions are <param> tags, NOT /// comments inside the parameter list.
// The latter compile with a CS1587 warning and are DISCARDED, so every field
// description would be absent from the generated document while looking present
// in the source. OpenApiContractTests asserts a known description survives into
// the spec, because that failure is invisible by construction.
//
// `document` stays a JsonElement everywhere. It is validated against the ENGINE
// CATALOG at runtime (DocumentValidator + /catalog/unit-op-types), so the legal
// unit-op types, ports and parameter names are a property of the running engine
// and cannot be baked into a compile-time type here. DocumentSchema below
// describes its fixed outer shape for the reader; nothing binds to it.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwsimRunner.Api;

// ── errors ────────────────────────────────────────────────────────────────

/// <summary>The body of every non-2xx response. <c>error</c> is the stable code to switch on.</summary>
/// <param name="Error">Stable machine-readable code, e.g. INVALID_REQUEST.</param>
/// <param name="Message">Human-readable detail. Do not parse.</param>
/// <param name="Detail">Engine context on worker-originated 400s. Absent otherwise.</param>
public sealed record ErrorResponse(
    string Error,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail = null);

/// <summary>
/// A document rejection. Carried by 400 DOCUMENT_INVALID (the API's structural pass) and by
/// 422 BUILD_FAILED / UNKNOWN_COMPOUND (the engine's). Check for <c>issues</c> on both.
/// </summary>
/// <param name="Error">DOCUMENT_INVALID, BUILD_FAILED or UNKNOWN_COMPOUND.</param>
/// <param name="Issues">Every problem found, collected in one pass.</param>
public sealed record DocumentErrorResponse(string Error, List<IssueResponse> Issues);

/// <summary>One problem found in a document, located by <c>tag</c> and/or <c>path</c>.</summary>
/// <param name="Severity">"error" or "warning". Only an error makes a document invalid.</param>
/// <param name="Code">e.g. MISSING_REQUIRED_PORT, INVALID_UNIT, DUPLICATE_TAG.</param>
/// <param name="Tag">The offending object's tag, when the issue belongs to one.</param>
/// <param name="Path">JSON path into the document, e.g. objects[2].</param>
/// <param name="Message">What is wrong, in words.</param>
public sealed record IssueResponse(
    string Severity, string Code, string? Tag, string? Path, string Message);

// ── health & templates ────────────────────────────────────────────────────

/// <summary>Readiness, engine identity and the curated template list in one call.</summary>
/// <param name="Ok">True when DWSIM was found. Mirrors <c>dwsimFound</c>.</param>
/// <param name="DwsimPath">Where the runner looked for the engine.</param>
/// <param name="DwsimFound">Whether DWSIM.Automation.dll is present at that path.</param>
/// <param name="DwsimVersion">
/// The DWSIM LIBRARY version. This cannot tell two runner builds apart — use <c>buildRef</c> for
/// that. Null when the assembly metadata is unreadable.
/// </param>
/// <param name="BuildRef">
/// Which runner build this is. "unknown" is an explicit value, never an absent field: an absent
/// field and a stale one read the same to a consumer.
/// </param>
/// <param name="SupportedRange">The DWSIM range this runner is validated against, e.g. "&gt;=9.0 &lt;10".</param>
/// <param name="VersionSupported">
/// False still solves — the result gains a best-effort warning rather than failing.
/// </param>
/// <param name="TemplatesPath">Directory of curated templates.</param>
/// <param name="Templates">Curated template ids only. GET /templates is the complete list.</param>
/// <param name="MaxConcurrent">
/// Effective worker pool size. The 429 QUEUE_FULL admission cap is 5x this. Read it here rather
/// than assuming: the code default is 4 and the shipped images set 6.
/// </param>
/// <param name="MaxEvaluations">The /optimize budget cap.</param>
/// <param name="MaxTimeoutSeconds">The largest timeoutSeconds any route will honour.</param>
/// <param name="FlowsheetProbe">
/// Whether the WORKER actually constructed a flowsheet on this image. ok/dwsimFound only say the
/// DWSIM files are on disk, so they stay true on an image whose engine cannot build.
/// </param>
/// <param name="Hint">Install instructions when dwsimFound is false; null otherwise.</param>
public sealed record HealthResponse(
    bool Ok, string DwsimPath, bool DwsimFound, string? DwsimVersion, string BuildRef,
    string SupportedRange, bool VersionSupported, ProbeReport FlowsheetProbe,
    string TemplatesPath, string?[] Templates,
    int MaxConcurrent, int MaxEvaluations, int MaxTimeoutSeconds, string? Hint);

/// <summary>The result of the background flowsheet-construction probe (ISK-104).</summary>
/// <param name="State">"pending" until the probe answers, then "ok" or "failed".</param>
/// <param name="ElapsedMs">How long the probe took; 0 while pending.</param>
/// <param name="CheckedAt">When it last answered, ISO-8601 UTC; null while pending.</param>
/// <param name="Error">The cause when state is "failed", so a red probe needs no deploy logs.</param>
public record ProbeReport(string State, long ElapsedMs, string? CheckedAt, string? Error)
{
    public static readonly ProbeReport Pending = new("pending", 0, null, null);
}

/// <summary>One template, curated or user-saved.</summary>
/// <param name="Id">The id used as templateId elsewhere.</param>
/// <param name="Source">"curated" (read-only) or "user" (deletable, overwritable).</param>
/// <param name="CreatedUtc">User templates only.</param>
/// <param name="SolvedAtSave">Whether the flowsheet had converged when saved. User templates only.</param>
public sealed record TemplateListItem(string Id, string Source, DateTime? CreatedUtc, bool? SolvedAtSave);

/// <summary>Object inventory of a template, without solving it.</summary>
/// <param name="Objects">Every addressable object in the flowsheet.</param>
public sealed record ObjectsResponse(List<ObjectInfoResponse> Objects);

/// <summary>One flowsheet object and what can be set on it.</summary>
/// <param name="Tag">The object's tag — the <c>object</c> of a /solve override.</param>
/// <param name="Type">The engine's type name for it.</param>
/// <param name="SettableProperties">
/// The legal <c>property</c> vocabulary for an override against this object. Anything else is
/// refused with INVALID_PROPERTY.
/// </param>
public sealed record ObjectInfoResponse(string Tag, string Type, List<string> SettableProperties);

// ── catalog ───────────────────────────────────────────────────────────────

/// <summary>Compounds this engine can resolve by name.</summary>
/// <param name="EngineVersion">The engine build these were read from.</param>
/// <param name="Compounds">Every available compound.</param>
public sealed record CompoundsResponse(string? EngineVersion, List<CompoundEntry> Compounds);

/// <summary>One compound.</summary>
/// <param name="Name">The name to use in a document's <c>compounds</c> array.</param>
/// <param name="Formula">Chemical formula, when the engine reports one.</param>
/// <param name="CasNumber">CAS registry number, when the engine reports one.</param>
public sealed record CompoundEntry(string Name, string? Formula, string? CasNumber);

/// <summary>Thermodynamic property packages this engine offers.</summary>
/// <param name="EngineVersion">The engine build these were read from.</param>
/// <param name="PropertyPackages">Every available package.</param>
public sealed record PropertyPackagesResponse(string? EngineVersion, List<PropertyPackageEntry> PropertyPackages);

/// <summary>One property package.</summary>
/// <param name="Id">The value to put in a document's <c>propertyPackage</c>.</param>
/// <param name="Name">Display name. Not always usable as an id.</param>
/// <param name="Description">What it models.</param>
public sealed record PropertyPackageEntry(string Id, string Name, string Description);

/// <summary>
/// Port and parameter schema per wire type — the source of truth for legal <c>type</c>,
/// <c>port</c> and <c>parameters</c> names in a document.
/// </summary>
/// <param name="EngineVersion">The engine build this was read from.</param>
/// <param name="UnitOpTypes">
/// Engine-driven and therefore open: the set of types and their parameters is a property of the
/// running engine, so it is not modelled as a fixed type here.
/// </param>
public sealed record UnitOpTypesResponse(string? EngineVersion, JsonElement UnitOpTypes);

/// <summary>What the ENGINE declares, versus what this runner exposes.</summary>
/// <param name="EngineVersion">The engine build this was read from.</param>
/// <param name="EngineInventory">Every unit-op kind the engine declares.</param>
public sealed record EngineInventoryResponse(string? EngineVersion, List<EngineInventoryEntryResponse> EngineInventory);

/// <summary>One unit-op kind the engine declares.</summary>
/// <param name="Name">The engine's internal type name.</param>
/// <param name="DisplayName">The engine's human-facing name.</param>
/// <param name="Source">Which engine assembly declares it.</param>
/// <param name="Instantiable">Whether the engine can construct one at all.</param>
/// <param name="ExposedAs">
/// This runner's wire type, or null when DWSIM has the unit op and this runner has no type for
/// it — an absent capability that says so, rather than a silent omission.
/// </param>
public sealed record EngineInventoryEntryResponse(
    string Name, string DisplayName, string Source, bool Instantiable, string? ExposedAs);

/// <summary>
/// The unit vocabulary this runner ACCEPTS, keyed by quantity kind. Published so a client's
/// transcribed copy can be checked rather than trusted: a disagreement drops a value before it
/// ships (client stricter) or refuses it with INVALID_UNIT after it does (runner stricter), and
/// both surface a long way from the edit that caused them.
/// </summary>
/// <param name="EngineVersion">The engine build present, for correlation.</param>
/// <param name="Units">
/// Quantity kind to accepted spellings, e.g. "pressure" to ["bar","Pa","kPa",...]. An empty list
/// means the kind takes no unit. Served from the dictionary that does the accepting, so it cannot
/// be a second opinion about it.
/// </param>
public sealed record UnitsResponse(string? EngineVersion, IReadOnlyDictionary<string, IReadOnlyList<string>> Units);

// ── solve results ─────────────────────────────────────────────────────────

/// <summary>
/// The result of a solve. <c>converged: false</c> is a 200 — a run that completed and diverged is
/// an answer, not an HTTP failure.
/// </summary>
/// <param name="Converged">Whether the engine reached a solution.</param>
/// <param name="ElapsedMs">
/// Engine time. On a cache hit this is the ORIGINAL solve's figure, not this call's — timing a
/// re-run measures the cache.
/// </param>
/// <param name="Streams">One row per material stream.</param>
/// <param name="Energy">One row per energy stream.</param>
/// <param name="UnitOps">One row per unit op.</param>
/// <param name="Warnings">
/// Non-fatal engine notes. Populated on divergence, and when the engine is outside the supported
/// range.
/// </param>
public record SolveResponse(
    bool Converged, long ElapsedMs, List<StreamRowResponse> Streams,
    List<EnergyRowResponse> Energy, List<UnitOpRowResponse> UnitOps, List<string> Warnings);

/// <summary>
/// One material stream. A null on any nullable field means the engine did not report it — never
/// an implied zero.
/// </summary>
/// <param name="Name">The stream's tag. Results are joined back on this.</param>
/// <param name="Phase">Derived from <c>phases</c>, never from an engine slot index.</param>
/// <param name="TemperatureC">Temperature in degrees Celsius.</param>
/// <param name="PressureBar">Absolute pressure in bar.</param>
/// <param name="MassFlowKgH">Mass flow in kg/h.</param>
/// <param name="MolarFlowKmolH">Molar flow in kmol/h.</param>
/// <param name="CompositionMol">Mole fractions by compound.</param>
/// <param name="DensityKgM3">Bulk density in kg/m3.</param>
/// <param name="CompositionMass">Mass fractions by compound — a separation is stated this way.</param>
/// <param name="VaporFraction">Molar vapour fraction.</param>
/// <param name="Phases">One block per phase actually present.</param>
public sealed record StreamRowResponse(
    string Name, string? Phase, double? TemperatureC, double? PressureBar,
    double? MassFlowKgH, double? MolarFlowKmolH, Dictionary<string, double>? CompositionMol,
    double? DensityKgM3, Dictionary<string, double>? CompositionMass,
    double? VaporFraction, List<StreamPhaseBlockResponse>? Phases);

/// <summary>One phase actually present, named in physics terms — never an engine slot index.</summary>
/// <param name="Name">"vapor", "liquid", "liquid2" or "solid".</param>
/// <param name="MoleFraction">This phase's share of the stream, molar.</param>
/// <param name="Composition">Mole fractions within this phase.</param>
/// <param name="DensityKgM3">Phase density in kg/m3.</param>
/// <param name="MolecularWeight">Phase mean molecular weight.</param>
/// <param name="HeatCapacityKJKgK">Phase Cp in kJ/(kg.K).</param>
/// <param name="ViscosityPaS">Phase viscosity in Pa.s.</param>
public sealed record StreamPhaseBlockResponse(
    string Name, double MoleFraction, Dictionary<string, double>? Composition,
    double? DensityKgM3, double? MolecularWeight, double? HeatCapacityKJKgK, double? ViscosityPaS);

/// <summary>One energy stream.</summary>
/// <param name="Name">The stream's tag.</param>
/// <param name="DutyKw">Duty in kW. Sign convention follows the engine.</param>
public sealed record EnergyRowResponse(string Name, double? DutyKw);

/// <summary>One unit op's computed results.</summary>
/// <param name="Name">The unit op's tag.</param>
/// <param name="Type">The engine's type name.</param>
/// <param name="PowerKw">Shaft power in kW, for rotating equipment.</param>
/// <param name="DutyKw">Thermal duty in kW, for heat transfer equipment.</param>
/// <param name="OutletTemperatureC">Computed outlet temperature.</param>
/// <param name="OutletPressureBar">Computed outlet pressure.</param>
/// <param name="SolvingMethod">The column solver used. Columns only; null on every other unit op.</param>
/// <param name="MaxIterations">The column iteration budget. Columns only.</param>
public sealed record UnitOpRowResponse(
    string Name, string Type, double? PowerKw, double? DutyKw,
    double? OutletTemperatureC, double? OutletPressureBar,
    string? SolvingMethod, int? MaxIterations);

/// <summary>A solve result plus what the build did, and where it was saved.</summary>
/// <param name="Converged">Whether the engine reached a solution.</param>
/// <param name="ElapsedMs">Engine time; the original figure on a cache hit.</param>
/// <param name="Streams">One row per material stream.</param>
/// <param name="Energy">One row per energy stream.</param>
/// <param name="UnitOps">One row per unit op.</param>
/// <param name="Warnings">Non-fatal engine notes.</param>
/// <param name="Build">What construction did before the solve.</param>
/// <param name="Template">Present only when saveAsTemplate was sent.</param>
public sealed record BuildSolveResponse(
    bool Converged, long ElapsedMs,
    List<StreamRowResponse> Streams, List<EnergyRowResponse> Energy,
    List<UnitOpRowResponse> UnitOps, List<string> Warnings,
    BuildInfoResponse Build, TemplateSaveResponse? Template)
    : SolveResponse(Converged, ElapsedMs, Streams, Energy, UnitOps, Warnings);

/// <summary>What building the document produced, before solving it.</summary>
/// <param name="ObjectsCreated">Streams and unit ops constructed.</param>
/// <param name="ConnectionsMade">Ports wired.</param>
/// <param name="ElapsedMs">Build time only, excluding the solve.</param>
public sealed record BuildInfoResponse(int ObjectsCreated, int ConnectionsMade, long ElapsedMs);

/// <summary>
/// The outcome of a saveAsTemplate. A failed save is NOT a failed solve: the solve returns 200
/// and this block reports <c>saved: false</c>. The solve is never lost to a persistence side effect.
/// </summary>
/// <param name="Id">The template id requested.</param>
/// <param name="Source">Always "user" — a save never produces a curated template.</param>
/// <param name="Saved">Whether the file is on disk.</param>
/// <param name="Reason">
/// STORE_UNAVAILABLE (the directory is not writable) or WRITE_FAILED (it is, and the write still
/// failed). Absent when saved.
/// </param>
public sealed record TemplateSaveResponse(
    string Id, string Source, bool Saved,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);

/// <summary>Per-case results, keyed by the name you supplied.</summary>
/// <param name="Results">
/// A value is EITHER a SolveResponse OR an ErrorResponse — discriminate on the presence of
/// <c>error</c>. One case failing does not fail the set.
/// </param>
public sealed record CompareResponse(Dictionary<string, JsonElement> Results);

/// <summary>The outcome of a golden-section search.</summary>
/// <param name="Best">The best point found, with its full solve result.</param>
/// <param name="Evaluations">Every point tried, in order.</param>
/// <param name="Converged">Whether the search met its tolerance rather than running out of budget.</param>
/// <param name="StoppedReason">Why the search ended, e.g. "tolerance" or "maxEvaluations".</param>
public sealed record OptimizeResponse(
    OptimizeBest Best, List<OptimizeEvaluation> Evaluations, bool Converged, string StoppedReason);

/// <summary>The winning point.</summary>
/// <param name="Value">The variable value that produced it.</param>
/// <param name="ObjectiveValue">The objective read off that solve.</param>
/// <param name="Result">The full solve result at this point.</param>
public sealed record OptimizeBest(double Value, double? ObjectiveValue, SolveResponse Result);

/// <summary>One point tried.</summary>
/// <param name="Value">The variable value.</param>
/// <param name="ObjectiveValue">
/// Null when the point did not converge or the objective was unreadable. Such a point is skipped
/// by the search rather than failing it.
/// </param>
/// <param name="Converged">Whether this evaluation converged with a readable objective.</param>
public sealed record OptimizeEvaluation(double Value, double? ObjectiveValue, bool Converged);

/// <summary>
/// Structural and (optionally) semantic validation. Validity is in the body, not the status —
/// a well-formed request describing an invalid document is still 200.
/// </summary>
/// <param name="Valid">False when any issue has severity "error".</param>
/// <param name="Issues">
/// Every problem found. Structural checks are collect-all, so one pass returns them all rather
/// than one per round trip.
/// </param>
public sealed record ValidationResponse(bool Valid, List<IssueResponse> Issues);

/// <summary>A single-point flash. No flowsheet involved.</summary>
/// <param name="VaporFraction">Molar vapour fraction of the result.</param>
/// <param name="TemperatureC">Resulting temperature, degrees Celsius.</param>
/// <param name="PressureBar">Resulting absolute pressure, bar.</param>
/// <param name="Phases">One entry per phase present.</param>
/// <param name="EnthalpyKJKg">Mixture specific enthalpy, kJ/kg.</param>
/// <param name="EntropyKJKgK">Mixture specific entropy, kJ/(kg.K).</param>
/// <param name="DensityKgM3">Mixture density, kg/m3, when the engine reports one.</param>
public sealed record FlashResponse(
    double VaporFraction, double? TemperatureC, double? PressureBar,
    List<FlashPhaseResponse> Phases, double? EnthalpyKJKg, double? EntropyKJKgK, double? DensityKgM3);

/// <summary>One phase of a flash result.</summary>
/// <param name="Phase">"vapor", "liquid", "liquid2" or "solid".</param>
/// <param name="MolarFraction">This phase's molar share.</param>
/// <param name="Composition">Mole fractions within this phase.</param>
public sealed record FlashPhaseResponse(string Phase, double MolarFraction, Dictionary<string, double> Composition);

// ── requests (bound) ──────────────────────────────────────────────────────

/// <summary>Validate a document. Always 200 when the request itself is well-formed.</summary>
/// <param name="Document">The flowsheet document. See DocumentSchema.</param>
/// <param name="Semantic">
/// Default true. False runs structural checks only — no worker spawn and no queue slot. Structural
/// errors short-circuit semantic ones either way, so a structurally invalid document never reaches
/// the engine.
/// </param>
public sealed record ValidateRequest(JsonElement Document, bool? Semantic);

/// <summary>Build a document into a flowsheet, solve it, optionally save it as a template.</summary>
/// <param name="Document">The flowsheet document. See DocumentSchema.</param>
/// <param name="TimeoutSeconds">
/// Default 120 — NOT SOLVE_TIMEOUT_SECONDS. CLAMPED to 5..600, unlike /solve which falls back to
/// its default on an out-of-range value.
/// </param>
/// <param name="SaveAsTemplate">Optional persistence of the built flowsheet.</param>
public sealed record BuildSolveRequest(
    JsonElement Document, int? TimeoutSeconds, SaveAsTemplateRequest? SaveAsTemplate);

/// <summary>
/// Persist the built flowsheet as a user template. Conflicts are refused with 409 BEFORE the
/// solve; an unwritable store is reported AFTER it as template.saved false.
/// </summary>
/// <param name="Id">
/// Must match ^[A-Za-z0-9._-]+$ and not collide with a curated template name.
/// </param>
/// <param name="Overwrite">
/// Replace an existing USER template of this id. Never overrides a curated one — that is always 409.
/// </param>
public sealed record SaveAsTemplateRequest(string Id, bool? Overwrite);

/// <summary>Render a document as a PNG. Auto-layout is applied when positions are absent.</summary>
/// <param name="Document">The flowsheet document. See DocumentSchema.</param>
public sealed record PfdRequest(JsonElement Document);

/// <summary>Solve a stored template with optional overrides.</summary>
/// <param name="TemplateId">A curated or user template id, e.g. "methanol_synthesis".</param>
/// <param name="Overrides">Property values to apply before solving.</param>
/// <param name="TimeoutSeconds">
/// 1..600. An out-of-range value FALLS BACK to the server default; it is not clamped.
/// </param>
public sealed record SolveRequestDto(
    string TemplateId, List<PropertyOverride>? Overrides, int? TimeoutSeconds);

/// <summary>
/// Fan out across 1..25 named cases, with per-case error isolation. A sweep is a compare whose
/// cases you expanded from a range; there is deliberately no /sweep endpoint.
/// </summary>
/// <param name="TemplateId">A template id. Mutually exclusive with <c>document</c>.</param>
/// <param name="Document">
/// An inline document. Mutually exclusive with <c>templateId</c> — supplying both is 400
/// CONFLICTING_PARAMETERS, supplying neither is 400 INVALID_REQUEST.
/// </param>
/// <param name="Cases">Case name to its overrides. Between 1 and 25 entries.</param>
/// <param name="TimeoutSeconds">
/// Per case, not for the whole set. cases x timeoutSeconds is checked against the runner's
/// aggregate budget before anything runs — over it is 400 WORK_BUDGET_EXCEEDED.
/// </param>
public sealed record CompareRequestDto(
    string? TemplateId, JsonElement? Document,
    Dictionary<string, List<PropertyOverride>?>? Cases, int? TimeoutSeconds);

/// <summary>
/// Golden-section search over one variable. Every evaluation is an ordinary cached solve, run
/// sequentially — the search is inherently sequential. Worst-case wall time is
/// maxEvaluations x timeoutSeconds, so bound it yourself for anything interactive.
/// </summary>
/// <param name="TemplateId">A template id. Mutually exclusive with <c>document</c>.</param>
/// <param name="Document">An inline document. Mutually exclusive with <c>templateId</c>.</param>
/// <param name="Variable">The knob to turn.</param>
/// <param name="Objective">The number to move.</param>
/// <param name="Tolerance">Defaults to (max - min) * 1e-3.</param>
/// <param name="MaxEvaluations">2..30, default 20.</param>
/// <param name="TimeoutSeconds">
/// Per evaluation, not for the whole search. maxEvaluations x timeoutSeconds is checked against the
/// runner's aggregate budget BEFORE the search starts — over it is 400 WORK_BUDGET_EXCEEDED, so the
/// maximums of both are not simultaneously available.
/// </param>
public sealed record OptimizeRequestDto(
    string? TemplateId, JsonElement? Document,
    OptimizeVariableRequest? Variable, OptimizeObjectiveRequest? Objective,
    double? Tolerance, int? MaxEvaluations, int? TimeoutSeconds);

/// <summary>The knob to turn.</summary>
/// <param name="Object">An object tag from the template or document.</param>
/// <param name="Property">A settable property on that object.</param>
/// <param name="Unit">Omit for SI. Applies to min, max and the values tried.</param>
/// <param name="Min">Lower bound. Must be finite and strictly less than <c>max</c>.</param>
/// <param name="Max">Upper bound. Must be finite.</param>
public sealed record OptimizeVariableRequest(
    string Object, string Property, string? Unit, double Min, double Max);

/// <summary>The number to move, read off each solve result.</summary>
/// <param name="Object">The object carrying the objective, e.g. a product stream tag.</param>
/// <param name="Property">The result field to read, e.g. "massFlowKgH".</param>
/// <param name="Direction">"minimize" or "maximize". Anything else is 400.</param>
public sealed record OptimizeObjectiveRequest(string Object, string Property, string Direction);

/// <summary>Single-point thermodynamics, no flowsheet.</summary>
/// <param name="Compounds">Engine compound names from GET /catalog/compounds. Must be non-empty.</param>
/// <param name="Composition">Feed composition. Fractions must sum to 1.</param>
/// <param name="PropertyPackage">A package id from GET /catalog/property-packages, e.g. "NRTL".</param>
/// <param name="FlashType">
/// TP | PH | PS | PVF | TVF, each requiring its matching specs. TH and TS are NOT supported —
/// they kill the worker process. On a PURE COMPOUND, TVF is accepted but insensitive to
/// vaporFraction (saturation pressure does not depend on it), returning identical results for
/// different fractions; prefer PVF, which is responsive.
/// </param>
/// <param name="Temperature">Required by TP and TVF.</param>
/// <param name="Pressure">Required by TP, PH, PS and PVF.</param>
/// <param name="Enthalpy">Required by PH.</param>
/// <param name="Entropy">Required by PS.</param>
/// <param name="VaporFraction">Dimensionless molar vapour fraction. Required by PVF and TVF.</param>
public sealed record FlashRequestDto(
    List<string>? Compounds, CompositionRequest? Composition, string? PropertyPackage,
    string? FlashType, QuantityRequest? Temperature, QuantityRequest? Pressure,
    QuantityRequest? Enthalpy, QuantityRequest? Entropy, QuantityRequest? VaporFraction);

/// <summary>A value with an optional unit.</summary>
/// <param name="Value">The magnitude.</param>
/// <param name="Unit">
/// Omit for SI. A spelling this runner does not know is refused with 400 INVALID_UNIT rather than
/// converted — DWSIM's converter returns an unknown unit's value UNCHANGED, so guessing produces a
/// number under the wrong dimension that converges. entropy accepts only kJ/[kg.K]; vaporFraction is
/// dimensionless and takes no unit at all. Read the vocabulary from GET /catalog/units.
/// </param>
public sealed record QuantityRequest(double Value, string? Unit);

/// <summary>A composition. Fractions must sum to 1.</summary>
/// <param name="Basis">"mole" or "mass".</param>
/// <param name="Fractions">Compound name to fraction.</param>
public sealed record CompositionRequest(string? Basis, Dictionary<string, double>? Fractions);

// ── document (described, not bound) ───────────────────────────────────────

/// <summary>
/// The fixed outer shape of a flowsheet document. NOTHING BINDS TO THIS TYPE — <c>document</c> is
/// carried as a raw JSON object and validated against the live engine catalog, because the legal
/// unit-op types, ports and parameter names are a property of the running engine rather than of
/// this assembly. It is declared so a caller can see the shape; read GET /catalog/unit-op-types
/// for the parts it deliberately cannot express. Objects, connections, reactions and raw size are
/// all capped (defaults 500 / 1000 / 200 / 200 KB); over any of them is DOCUMENT_TOO_LARGE.
/// </summary>
/// <param name="SchemaVersion">Required. Must be 1.</param>
/// <param name="Name">Free-text label for the flowsheet.</param>
/// <param name="Compounds">Engine compound names — GET /catalog/compounds.</param>
/// <param name="PropertyPackage">A package id — GET /catalog/property-packages, e.g. "STEAM".</param>
/// <param name="ReactionSets">Required by reactor types that declare requiresReactionSet.</param>
/// <param name="Objects">Streams and unit ops. Tags must be unique.</param>
/// <param name="Connections">How the objects are wired together.</param>
public sealed record DocumentSchema(
    int SchemaVersion, string? Name, List<string>? Compounds, string? PropertyPackage,
    List<JsonElement>? ReactionSets, List<DocumentObject>? Objects, List<DocumentConnection>? Connections);

/// <summary>
/// A stream or unit op. <c>tag</c> is the join key results are folded back on and must be unique.
/// An OUTLET stream is declared with no <c>spec</c> — it is what the solve produces.
/// </summary>
/// <param name="Tag">Unique identifier within the document.</param>
/// <param name="Kind">"materialStream", "energyStream" or "unitOp".</param>
/// <param name="Type">Unit ops only. A wire type from GET /catalog/unit-op-types, e.g. "heater".</param>
/// <param name="Spec">
/// Material streams only: temperature, pressure, massFlow, composition and so on, each a
/// { value, unit } quantity. Omit entirely for an outlet.
/// </param>
/// <param name="Parameters">
/// Unit ops only. Legal names are catalog-driven per type, so this stays an open object.
/// </param>
public sealed record DocumentObject(
    string Tag, string Kind, string? Type, JsonElement? Spec, JsonElement? Parameters);

/// <summary>One connection between two objects.</summary>
/// <param name="From">The source object's tag.</param>
/// <param name="To">The destination object's tag.</param>
/// <param name="Port">
/// A port declared by the destination type, e.g. "Inlet". Every port the type marks required must
/// be connected or the document is refused with MISSING_REQUIRED_PORT.
/// </param>
public sealed record DocumentConnection(string From, string To, string Port);
