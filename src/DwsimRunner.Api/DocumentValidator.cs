// dwsim-runner API — GPL-3.0
// Structural validation of flowsheet documents (FR-VAL-001/003): pure JSON
// checks against the engine catalog's port/parameter map — no DWSIM types, no
// worker spawn. Collect-all: every issue found is reported; a structurally
// invalid document is never sent to the engine. Codes per
// specs/002-dwsim-mcp-tools/data-model.md.

using System.Text.Json;

namespace DwsimRunner.Api;

public sealed record ValidationIssue(string Severity, string Code, string? Tag, string? Path, string Message);

public sealed record CatalogPort(string Name, string Direction, string Accepts, bool Required);
public sealed record CatalogParameter(string Name, string? UnitType, bool Required);
public sealed record CatalogUnitOpType(string Type, List<CatalogPort> Ports, List<CatalogParameter> Parameters, bool RequiresReactionSet);

/// <summary>Parsed view of the worker `catalog` payload — the single source of
/// truth for unit-op ports and parameters on the API side.</summary>
public sealed class CatalogModel
{
    public string? EngineVersion { get; private init; }
    public Dictionary<string, CatalogUnitOpType> UnitOpTypes { get; } = new(StringComparer.Ordinal);

    public static CatalogModel Parse(string catalogJson)
    {
        using var doc = JsonDocument.Parse(catalogJson);
        var root = doc.RootElement;
        var model = new CatalogModel
        {
            EngineVersion = root.TryGetProperty("engineVersion", out var ev) ? ev.GetString() : null,
        };
        if (root.TryGetProperty("unitOpTypes", out var types) && types.ValueKind == JsonValueKind.Array)
            foreach (var t in types.EnumerateArray())
            {
                var type = t.GetProperty("type").GetString()!;
                var ports = t.TryGetProperty("ports", out var ps) && ps.ValueKind == JsonValueKind.Array
                    ? ps.EnumerateArray().Select(p => new CatalogPort(
                        p.GetProperty("name").GetString()!,
                        p.GetProperty("direction").GetString()!,
                        p.TryGetProperty("accepts", out var a) ? a.GetString()! : "material",
                        p.TryGetProperty("required", out var r) && r.GetBoolean())).ToList()
                    : [];
                var parameters = t.TryGetProperty("parameters", out var prs) && prs.ValueKind == JsonValueKind.Array
                    ? prs.EnumerateArray().Select(p => new CatalogParameter(
                        p.GetProperty("name").GetString()!,
                        p.TryGetProperty("unitType", out var ut) ? ut.GetString() : null,
                        p.TryGetProperty("required", out var rq) && rq.GetBoolean())).ToList()
                    : [];
                var requiresRx = t.TryGetProperty("requiresReactionSet", out var rr) && rr.GetBoolean();
                model.UnitOpTypes[type] = new CatalogUnitOpType(type, ports, parameters, requiresRx);
            }
        return model;
    }
}

public static class DocumentValidator
{
    public const int MaxObjects = 100;
    public const int MaxDocumentBytes = 200 * 1024;
    private const int SupportedSchemaVersion = 1;

    // The unit vocabulary shared with spec-001 overrides. Empty/absent unit is
    // allowed (SI assumed); anything else must be a known spelling.
    private static readonly Dictionary<string, string[]> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperature"] = ["C", "K", "F"],
        ["pressure"] = ["bar", "Pa", "kPa", "MPa", "atm", "psi", "mbar"],
        ["massFlow"] = ["kg/h", "kg/s", "kg/min", "kg/d", "t/h", "g/s", "lb/h"],
        ["molarFlow"] = ["kmol/h", "kmol/s", "mol/s", "mol/h", "lbmol/h"],
        ["volumetricFlow"] = ["m3/h", "m3/s", "L/min", "L/s", "L/h", "ft3/s"],
        ["enthalpy"] = ["kJ/kg", "J/kg", "BTU/lb"],
        ["power"] = ["kW", "W", "MW", "hp"],
        ["volume"] = ["m3", "L", "ft3"],
        ["length"] = ["m", "mm", "cm", "in", "ft"],
        ["dimensionless"] = [],
        ["integer"] = [],
        ["string"] = [],
    };

    private static readonly HashSet<string> AllKnownUnits =
        Units.Values.SelectMany(u => u).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<ValidationIssue> ValidateStructural(JsonElement doc, CatalogModel catalog)
    {
        var issues = new List<ValidationIssue>();
        void Error(string code, string? tag, string? path, string message) =>
            issues.Add(new ValidationIssue("error", code, tag, path, message));

        if (doc.ValueKind != JsonValueKind.Object)
        {
            Error("SCHEMA_INVALID", null, "$", "document must be a JSON object");
            return issues;
        }

        // ── size caps ─────────────────────────────────────────────────────
        if (doc.GetRawText().Length > MaxDocumentBytes)
            Error("DOCUMENT_TOO_LARGE", null, "$", $"document exceeds {MaxDocumentBytes / 1024} KB");

        // ── schema version ────────────────────────────────────────────────
        if (!doc.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number)
            Error("SCHEMA_INVALID", null, "schemaVersion", "schemaVersion is required and must be a number");
        else if (sv.GetInt32() != SupportedSchemaVersion)
            Error("UNSUPPORTED_SCHEMA", null, "schemaVersion",
                $"schemaVersion {sv.GetInt32()} is not supported (supported: {SupportedSchemaVersion})");

        // ── compounds / property package ──────────────────────────────────
        var compoundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.TryGetProperty("compounds", out var compounds) || compounds.ValueKind != JsonValueKind.Array
            || compounds.GetArrayLength() == 0)
            Error("SCHEMA_INVALID", null, "compounds", "compounds must be a non-empty array of names (1–50)");
        else
        {
            if (compounds.GetArrayLength() > 50)
                Error("SCHEMA_INVALID", null, "compounds", "compounds is limited to 50 entries");
            foreach (var c in compounds.EnumerateArray())
                if (c.GetString() is { Length: > 0 } name) compoundNames.Add(name);
        }

        if (!doc.TryGetProperty("propertyPackage", out var pp) || pp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pp.GetString()))
            Error("SCHEMA_INVALID", null, "propertyPackage", "propertyPackage (catalog id, e.g. 'PR') is required");

        // ── objects ───────────────────────────────────────────────────────
        var objectsByTag = new Dictionary<string, (string Kind, string? Type, int Index)>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var objects = doc.TryGetProperty("objects", out var objsEl) && objsEl.ValueKind == JsonValueKind.Array
            ? objsEl.EnumerateArray().ToList() : null;
        if (objects is null)
        {
            Error("SCHEMA_INVALID", null, "objects", "objects must be an array");
            return issues;
        }

        if (objects.Count > MaxObjects)
            Error("DOCUMENT_TOO_LARGE", null, "objects", $"document has {objects.Count} objects (max {MaxObjects})");

        for (var i = 0; i < objects.Count; i++)
        {
            var path = $"objects[{i}]";
            var o = objects[i];
            var tag = o.TryGetProperty("tag", out var tg) ? tg.GetString() : null;
            if (tag is not { Length: >= 1 and <= 64 })
            {
                Error("SCHEMA_INVALID", tag, path, "tag is required (1–64 chars)");
                continue;
            }
            if (!objectsByTag.TryAdd(tag, ("", null, i)) && duplicates.Add(tag))
                Error("DUPLICATE_TAG", tag, path, $"tag '{tag}' is used by more than one object");

            var kind = o.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind is not ("materialStream" or "energyStream" or "unitOp"))
            {
                Error("SCHEMA_INVALID", tag, $"{path}.kind", "kind must be materialStream | energyStream | unitOp");
                continue;
            }

            string? type = null;
            if (kind == "unitOp")
            {
                type = o.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type is null)
                    Error("SCHEMA_INVALID", tag, $"{path}.type", "unitOp requires a type");
                else if (!catalog.UnitOpTypes.TryGetValue(type, out _))
                    Error("UNKNOWN_UNIT_OP_TYPE", tag, $"{path}.type",
                        $"unknown unit-op type '{type}'; known types: {string.Join(", ", catalog.UnitOpTypes.Keys.Order())}");
                else
                {
                    var typeInfo = catalog.UnitOpTypes[type];
                    HashSet<string> given = o.TryGetProperty("parameters", out var prms) && prms.ValueKind == JsonValueKind.Object
                        ? prms.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : new(StringComparer.OrdinalIgnoreCase);
                    foreach (var reqParam in typeInfo.Parameters.Where(p => p.Required && !given.Contains(p.Name)))
                        Error("MISSING_REQUIRED_PARAMETER", tag, $"{path}.parameters",
                            $"'{type}' requires parameter '{reqParam.Name}'");
                    ValidateParameterUnits(o, tag, path, typeInfo, Error);
                    if (type is "distillationColumn" or "shortcutColumn")
                        ValidateColumnRules(o, tag, path, type, Error);
                    if (type is "heater" or "cooler")
                        ValidateHeaterCoolerRules(o, tag, path, type, Error);
                }
            }
            objectsByTag[tag] = (kind, type, i);

            if (kind == "materialStream" && o.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object)
                ValidateStreamSpec(spec, tag, path, compoundNames, Error);
        }

        // ── connections ───────────────────────────────────────────────────
        var portUse = new Dictionary<(string Tag, string Port), int>();
        var streamInbound = new Dictionary<string, int>(StringComparer.Ordinal);
        var streamOutbound = new Dictionary<string, int>(StringComparer.Ordinal);

        var connections = doc.TryGetProperty("connections", out var connsEl) && connsEl.ValueKind == JsonValueKind.Array
            ? connsEl.EnumerateArray().ToList() : [];
        for (var i = 0; i < connections.Count; i++)
        {
            var path = $"connections[{i}]";
            var c = connections[i];
            var from = c.TryGetProperty("from", out var f) ? f.GetString() : null;
            var to = c.TryGetProperty("to", out var tt) ? tt.GetString() : null;
            var port = c.TryGetProperty("port", out var pt) ? pt.GetString() : null;

            var fromKnown = from is not null && objectsByTag.ContainsKey(from);
            var toKnown = to is not null && objectsByTag.ContainsKey(to);
            if (!fromKnown) Error("UNRESOLVED_REFERENCE", from, $"{path}.from", $"no object tagged '{from}'");
            if (!toKnown) Error("UNRESOLVED_REFERENCE", to, $"{path}.to", $"no object tagged '{to}'");
            if (!fromKnown || !toKnown) continue;

            var (fromKind, fromType, _) = objectsByTag[from!];
            var (toKind, toType, _) = objectsByTag[to!];
            var fromIsUnit = fromKind == "unitOp";
            var toIsUnit = toKind == "unitOp";
            if (fromIsUnit == toIsUnit)
            {
                Error("SCHEMA_INVALID", from, path,
                    "exactly one connection endpoint must be a unit operation (stream ↔ unit op)");
                continue;
            }

            var unitTag = fromIsUnit ? from! : to!;
            var unitType = fromIsUnit ? fromType : toType;
            var streamTag = fromIsUnit ? to! : from!;
            var streamKind = fromIsUnit ? toKind : fromKind;
            var expectedDirection = fromIsUnit ? "out" : "in";   // stream→unit uses an inlet port
            var expectedAccepts = streamKind == "energyStream" ? "energy" : "material";

            if (unitType is null || !catalog.UnitOpTypes.TryGetValue(unitType, out var unitInfo))
                continue;   // already reported as UNKNOWN_UNIT_OP_TYPE

            var portInfo = unitInfo.Ports.FirstOrDefault(p => string.Equals(p.Name, port, StringComparison.OrdinalIgnoreCase));
            if (port is null || portInfo is null)
            {
                Error("UNKNOWN_PORT", unitTag, $"{path}.port",
                    $"{unitType} '{unitTag}' has no port '{port}'; valid ports: {string.Join(", ", unitInfo.Ports.Select(p => p.Name))}");
                continue;
            }
            if (portInfo.Direction != expectedDirection || portInfo.Accepts != expectedAccepts)
                Error("UNKNOWN_PORT", unitTag, $"{path}.port",
                    $"port '{portInfo.Name}' on '{unitTag}' is {portInfo.Direction}/{portInfo.Accepts}; " +
                    $"this connection needs {expectedDirection}/{expectedAccepts}");

            var key = (unitTag, portInfo.Name);
            portUse[key] = portUse.GetValueOrDefault(key) + 1;
            if (portUse[key] == 2)
                Error("PORT_CONFLICT", unitTag, path, $"port '{portInfo.Name}' on '{unitTag}' is connected more than once");

            var dirMap = fromIsUnit ? streamInbound : streamOutbound;
            dirMap[streamTag] = dirMap.GetValueOrDefault(streamTag) + 1;
            if (dirMap[streamTag] == 2)
                Error("PORT_CONFLICT", streamTag, path,
                    $"stream '{streamTag}' has more than one {(fromIsUnit ? "source" : "destination")} connection");
        }

        // ── reactions / reaction sets ─────────────────────────────────────
        var reactionTags = new HashSet<string>(StringComparer.Ordinal);
        if (doc.TryGetProperty("reactions", out var rxs) && rxs.ValueKind == JsonValueKind.Array)
            foreach (var rx in rxs.EnumerateArray())
                if (rx.TryGetProperty("tag", out var rtag) && rtag.GetString() is { } rt)
                    reactionTags.Add(rt);

        var reactorsWithSets = new HashSet<string>(StringComparer.Ordinal);
        if (doc.TryGetProperty("reactionSets", out var sets) && sets.ValueKind == JsonValueKind.Array)
        {
            var si = 0;
            foreach (var set in sets.EnumerateArray())
            {
                var path = $"reactionSets[{si++}]";
                if (set.TryGetProperty("reactions", out var refs) && refs.ValueKind == JsonValueKind.Array)
                    foreach (var r in refs.EnumerateArray())
                        if (r.GetString() is { } rref && !reactionTags.Contains(rref))
                            Error("UNRESOLVED_REFERENCE", rref, $"{path}.reactions", $"no reaction tagged '{rref}'");
                if (set.TryGetProperty("attachTo", out var attach) && attach.ValueKind == JsonValueKind.Array)
                    foreach (var a in attach.EnumerateArray())
                        if (a.GetString() is { } atag)
                        {
                            if (!objectsByTag.ContainsKey(atag))
                                Error("UNRESOLVED_REFERENCE", atag, $"{path}.attachTo", $"no object tagged '{atag}'");
                            else reactorsWithSets.Add(atag);
                        }
            }
        }

        foreach (var (tag, (kind, type, _)) in objectsByTag)
            if (kind == "unitOp" && type is not null
                && catalog.UnitOpTypes.TryGetValue(type, out var info) && info.RequiresReactionSet
                && !reactorsWithSets.Contains(tag))
                Error("MISSING_REACTION_SET", tag, null,
                    $"{type} '{tag}' requires a reaction set (add one under reactionSets with attachTo: [\"{tag}\"])");

        return issues;
    }

    private static void ValidateStreamSpec(JsonElement spec, string tag, string path,
        HashSet<string> compoundNames, Action<string, string?, string?, string> error)
    {
        foreach (var (quantity, prop) in new[]
                 { ("temperature", "temperature"), ("pressure", "pressure"), ("massFlow", "massFlow"),
                   ("molarFlow", "molarFlow"), ("volumetricFlow", "volumetricFlow"), ("enthalpy", "enthalpy") })
            if (spec.TryGetProperty(prop, out var q) && q.ValueKind == JsonValueKind.Object
                && q.TryGetProperty("unit", out var u) && u.GetString() is { Length: > 0 } unit
                && !Units[quantity].Contains(unit, StringComparer.OrdinalIgnoreCase))
                error("INVALID_UNIT", tag, $"{path}.spec.{prop}.unit",
                    $"unknown {quantity} unit '{unit}'; accepted: {string.Join(", ", Units[quantity])}");

        if (spec.TryGetProperty("composition", out var comp) && comp.ValueKind == JsonValueKind.Object)
        {
            if (comp.TryGetProperty("basis", out var basis)
                && basis.GetString() is { } b && b is not ("molar" or "mass"))
                error("SCHEMA_INVALID", tag, $"{path}.spec.composition.basis", "basis must be molar | mass");

            if (comp.TryGetProperty("fractions", out var fr) && fr.ValueKind == JsonValueKind.Object)
            {
                double sum = 0;
                foreach (var f in fr.EnumerateObject())
                {
                    if (f.Value.ValueKind != JsonValueKind.Number)
                    {
                        error("SCHEMA_INVALID", tag, $"{path}.spec.composition.fractions", $"fraction '{f.Name}' must be a number");
                        continue;
                    }
                    sum += f.Value.GetDouble();
                    if (compoundNames.Count > 0 && !compoundNames.Contains(f.Name))
                        error("UNRESOLVED_REFERENCE", tag, $"{path}.spec.composition.fractions",
                            $"composition names '{f.Name}', which is not in the document's compounds list");
                }
                if (Math.Abs(sum - 1.0) > 1e-4)
                    error("COMPOSITION_NOT_NORMALIZED", tag, $"{path}.spec.composition",
                        $"fractions sum to {sum:G6}; they must sum to 1 (±1e-4)");
            }
        }
    }

    // Column-specific structural rules (T032/US2): the feed must land on an
    // existing stage, reflux must be positive, and the condenser cannot sit at
    // a higher pressure than the reboiler (pressure increases down a column).
    private static readonly Dictionary<string, double> PressureToPa = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pa"] = 1, ["kPa"] = 1e3, ["MPa"] = 1e6, ["bar"] = 1e5,
        ["mbar"] = 100, ["atm"] = 101325, ["psi"] = 6894.757,
    };

    private static void ValidateColumnRules(JsonElement obj, string tag, string path,
        string type, Action<string, string?, string?, string> error)
    {
        if (!obj.TryGetProperty("parameters", out var prms) || prms.ValueKind != JsonValueKind.Object) return;

        static double? Numeric(JsonElement prms, string name)
        {
            if (!prms.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner)
                && inner.ValueKind == JsonValueKind.Number) return inner.GetDouble();
            return null;
        }
        static string? Unit(JsonElement prms, string name) =>
            prms.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("unit", out var u) ? u.GetString() : null;

        if (type == "distillationColumn")
        {
            var stages = Numeric(prms, "numberOfStages");
            var feedStage = Numeric(prms, "feedStage");
            if (stages is < 3)
                error("INVALID_PARAMETER_VALUE", tag, $"{path}.parameters.numberOfStages",
                    $"numberOfStages is {stages}; a column needs at least 3 stages");
            if (feedStage is <= 0)
                error("INVALID_PARAMETER_VALUE", tag, $"{path}.parameters.feedStage",
                    $"feedStage is {feedStage}; it must be ≥ 1");
            else if (feedStage is { } fsv && stages is { } st && fsv > st)
                error("INVALID_PARAMETER_VALUE", tag, $"{path}.parameters.feedStage",
                    $"feedStage {fsv} is outside the column ({st} stages)");
        }

        if (Numeric(prms, "refluxRatio") is { } rr && rr <= 0)
            error("INVALID_PARAMETER_VALUE", tag, $"{path}.parameters.refluxRatio",
                $"refluxRatio is {rr}; it must be > 0");

        double? ToPa(string name)
        {
            if (Numeric(prms, name) is not { } value) return null;
            var unit = Unit(prms, name);
            if (unit is null or { Length: 0 }) return value;   // bare number = SI (Pa)
            return PressureToPa.TryGetValue(unit, out var factor) ? value * factor : null;   // unknown unit already flagged
        }
        if (ToPa("condenserPressure") is { } cond && ToPa("reboilerPressure") is { } reb && cond > reb)
            error("INVALID_PARAMETER_VALUE", tag, $"{path}.parameters.condenserPressure",
                "condenser pressure exceeds reboiler pressure; pressure must not decrease down the column");
    }

    // Heater/cooler mode-conflict rule (spec 005 FR-FIX-004): outletTemperature
    // and heatDuty select mutually exclusive engine calculation modes — the
    // engine would silently honor only one of them.
    private static void ValidateHeaterCoolerRules(JsonElement obj, string tag, string path,
        string type, Action<string, string?, string?, string> error)
    {
        if (!obj.TryGetProperty("parameters", out var prms) || prms.ValueKind != JsonValueKind.Object) return;
        var names = prms.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("outletTemperature") && names.Contains("heatDuty"))
            error("CONFLICTING_PARAMETERS", tag, $"{path}.parameters",
                $"Object '{tag}' ({type}) has conflicting parameters: 'outletTemperature' and 'heatDuty' " +
                "are mutually exclusive — specify one or the other, not both.");
    }

    private static void ValidateParameterUnits(JsonElement obj, string tag, string path,
        CatalogUnitOpType typeInfo, Action<string, string?, string?, string> error)
    {
        if (!obj.TryGetProperty("parameters", out var prms) || prms.ValueKind != JsonValueKind.Object) return;
        foreach (var p in prms.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object
                || !p.Value.TryGetProperty("unit", out var u) || u.GetString() is not { Length: > 0 } unit)
                continue;
            var catalogParam = typeInfo.Parameters.FirstOrDefault(cp =>
                string.Equals(cp.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            var allowed = catalogParam?.UnitType is { } ut && Units.TryGetValue(ut, out var us) ? us : null;
            if (allowed is { Length: > 0 } && !allowed.Contains(unit, StringComparer.OrdinalIgnoreCase))
                error("INVALID_UNIT", tag, $"{path}.parameters.{p.Name}.unit",
                    $"unknown {catalogParam!.UnitType} unit '{unit}'; accepted: {string.Join(", ", allowed)}");
            else if (allowed is null && !AllKnownUnits.Contains(unit))
                error("INVALID_UNIT", tag, $"{path}.parameters.{p.Name}.unit", $"unknown unit '{unit}'");
        }
    }
}
