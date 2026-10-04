// dwsim-runner Worker — GPL-3.0
// iskra spec 285 (ISK-485) — `read` mode: a DWSIM file → this runner's own vocabulary.
//
// The INVERSE of FlowsheetBuilder: the file is loaded with DWSIM's own loader and every
// simulation object is put in exactly one of three places —
//   document.objects  what build-solve can rebuild: streams and catalog unit ops, with the file's
//                     SPECIFICATIONS (feeds, unit-op parameters the runner reads back),
//   placeholders      blocks the catalog cannot state (CAPE-OPEN, script, logical, other types),
//   ignored           graphics that are not process objects (tables, text, images).
// The file's RESULTS go to `stored` and never into `document` (GP-3): a stream's state is a
// specification only on a feed, a unit-op value only when the unit's calculation mode reads it.
//
// The API sniffed the format and named the file accordingly; the loader picks zip vs XML by
// extension (Automation3.LoadFlowsheet: an extension ending in "z" is unzipped).

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.Thermodynamics.Streams;
using DWSIM.UnitOperations.Streams;
using DWSIM.UnitOperations.UnitOperations;
using SepOps = DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps;

namespace DwsimRunner.Worker;

internal static partial class Reader
{
    private const int MaxChainChars = 2048;

    public static JsonObject Read(Job job)
    {
        var path = job.Template ?? throw new WorkerInputException("INVALID_REQUEST", "template (the file path) is required for read mode");
        var auto = new DWSIM.Automation.Automation3();
        object? loaded;
        try { loaded = auto.LoadFlowsheet(path); }
        catch (Exception ex) { throw new TemplateLoadException(Chain(ex), "LOAD_FAILED"); }
        var fs = loaded as IFlowsheet
                 ?? throw new TemplateLoadException("the engine's loader returned no flowsheet", "LOAD_FAILED");

        var result = Describe(fs, Engine.PropertyPackageNames(auto).Names);
        result["savedBy"] = SavedBy(path);
        result["engineVersion"] = Modes.ExtractVersion(auto);
        return result;
    }

    /// <summary>
    /// The FULL exception chain, one "Type: message" line per level, innermost last, capped. The
    /// 14 FOSSEE files that failed under `inspect` said only "The type initializer for
    /// 'DWSIM.Logging.Logger' threw an exception" because the cause was one level down (R4).
    /// </summary>
    internal static string Chain(Exception ex)
    {
        var lines = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AggregateException { InnerExceptions.Count: > 1 } agg)
            {
                lines.Add($"{e.GetType().FullName}: {e.Message}");
                lines.AddRange(agg.InnerExceptions.Skip(1).Select(i => $"  (also) {i.GetType().FullName}: {i.Message}"));
                continue;
            }
            lines.Add($"{e.GetType().FullName}: {e.Message}");
        }
        var text = string.Join("\n", lines.Select((l, i) => new string(' ', 2 * Math.Min(i, 8)) + RedactPaths(l.Trim())));
        return text.Length <= MaxChainChars ? text : text[..(MaxChainChars - 1)] + "…";
    }

    /// <summary>#31 review — an absolute path (quoted, or a bare /-rooted run) reads as &lt;path&gt;; the
    /// cause stays. The caller learns WHY a load failed, not where the runner keeps its files.</summary>
    internal static string RedactPaths(string s) =>
        AbsolutePath().Replace(s, m => m.Groups["q"].Success ? $"{m.Groups["q"].Value}<path>{m.Groups["q"].Value}" : "<path>");

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<q>['""])/[^'""]*\k<q>|(?<![\w.])/(?:[\w.\-]+/)+[\w.\-]*")]
    private static partial System.Text.RegularExpressions.Regex AbsolutePath();

    /// <summary>"DWSIM &lt;BuildVersion&gt;" from the file's own GeneralInfo, or null.</summary>
    internal static string? SavedBy(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            ZipArchive? zip = null;
            Stream xml = file;
            if (path.EndsWith("z", StringComparison.OrdinalIgnoreCase))
            {
                zip = new ZipArchive(file, ZipArchiveMode.Read);
                xml = zip.Entries.First(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Open();
            }
            using (zip)
            using (var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == "BuildVersion") return "DWSIM " + reader.ReadElementContentAsString().Trim();
                    if (reader.Name == "SimulationObjects") return null;   // GeneralInfo comes first
                }
            }
        }
        catch (Exception) { /* a missing version is null, not a failed read */ }
        return null;
    }

    // ── the description ─────────────────────────────────────────────────────

    internal static JsonObject Describe(IFlowsheet fs, IReadOnlyCollection<string> enginePackageNames)
    {
        var warnings = new List<string>();
        var tags = UniqueTags(fs, warnings);   // sim-object name → document tag

        // compounds — the catalog's spelling, resolved case-insensitively (R5: `1-butene` in a
        // runner-saved file against `1-Butene` in the catalog)
        var compoundName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fs.SelectedCompounds.Keys)
        {
            var resolved = Engine.ResolveCompound(fs.AvailableCompounds.Keys, name, out _);
            if (resolved is null) warnings.Add($"compound '{name}' is not in this engine's catalog; kept as the file spells it");
            compoundName[name] = resolved ?? name;
        }

        // property packages
        var packages = new JsonArray();
        var ppUse = new Dictionary<string, int>();
        foreach (var so in fs.SimulationObjects.Values)
            if (SafeGet(() => so.PropertyPackage) is { } used)
                ppUse[used.UniqueID ?? used.Name] = ppUse.GetValueOrDefault(used.UniqueID ?? used.Name) + 1;
        string? documentPackage = null;
        var bestUse = -1;
        foreach (var pp in fs.PropertyPackages.Values)
        {
            var (id, supported) = PackageId(pp, enginePackageNames);
            packages.Add(new JsonObject { ["name"] = PackageName(pp), ["id"] = id, ["supported"] = supported });
            var use = ppUse.GetValueOrDefault(pp.UniqueID ?? pp.Name);
            if (use > bestUse) { bestUse = use; documentPackage = id; }
        }
        if (fs.PropertyPackages.Count > 1)
            warnings.Add($"the file has {fs.PropertyPackages.Count} property packages; document.propertyPackage is the one most objects use ('{documentPackage}')");

        // classification — each simulation object lands in exactly ONE bucket
        var objects = new JsonArray();
        var connections = new JsonArray();
        var placeholders = new JsonArray();
        var ignored = new JsonArray();
        var layout = new JsonObject();
        var unitModes = new Dictionary<string, string?>();   // unit tag → calc mode, for energy-stream specs

        foreach (var so in fs.SimulationObjects.Values.OrderBy(o => tags[o.Name], StringComparer.Ordinal))
        {
            var tag = tags[so.Name];
            var go = so.GraphicObject;
            if (go is not null)
                layout[tag] = new JsonObject { ["x"] = go.X, ["y"] = go.Y, ["w"] = go.Width, ["h"] = go.Height };

            switch (so)
            {
                case MaterialStream:
                    objects.Add(new JsonObject { ["tag"] = tag, ["kind"] = "materialStream" });
                    continue;
                case EnergyStream:
                    objects.Add(new JsonObject { ["tag"] = tag, ["kind"] = "energyStream" });
                    continue;
            }

            // The graphic's ObjectType is not the unit's type for every class — measured: a loaded
            // Mixer's graphic says NodeIn and a Splitter's NodeOut — so the wire type comes from
            // Engine.FriendlyType, which falls back to the CLR class name.
            var engineType = go?.ObjectType ?? ObjectType.Nenhum;
            if (Enum.TryParse<ObjectType>(so.GetType().Name, out var byClass)) engineType = byClass;
            var friendly = Engine.FriendlyType(so);
            var wire = UnitOpCatalog.Types.ContainsKey(friendly) ? friendly : null;
            var reason = wire is null ? PlaceholderReason(engineType) : null;
            string? detail = null;

            JsonObject? parameters = null;
            List<(string Stream, string Port)> wired = [];
            if (wire is not null)
            {
                var def = UnitOpCatalog.Types[wire];
                if (so is DistillationColumn col)
                    (parameters, wired, detail) = ReadColumn(col, tags);
                else
                {
                    parameters = ReadParameters(so, def, out var mode);
                    unitModes[tag] = mode;
                    (wired, detail) = MapPorts(go!, def, tags);
                }
                if (detail is not null) reason = "NOT_IN_CATALOG";   // a configuration the catalog cannot state
            }

            if (reason is not null)
            {
                placeholders.Add(Placeholder(so, tag, engineType, reason, detail, tags));
                continue;
            }

            var unit = new JsonObject { ["tag"] = tag, ["kind"] = "unitOp", ["type"] = wire };
            if (parameters is { Count: > 0 }) unit["parameters"] = parameters;
            objects.Add(unit);
            foreach (var (stream, port) in wired)
            {
                var portDef = UnitOpCatalog.Types[wire!].Ports.First(p => p.Name == port);
                connections.Add(portDef.Direction == "in"
                    ? new JsonObject { ["from"] = stream, ["to"] = tag, ["port"] = port }
                    : new JsonObject { ["from"] = tag, ["to"] = stream, ["port"] = port });
            }
        }

        foreach (var go in fs.GraphicObjects.Values)
        {
            if (go.IsConnector || fs.SimulationObjects.ContainsKey(go.Name)) continue;
            ignored.Add(new JsonObject
            {
                ["tag"] = string.IsNullOrEmpty(go.Tag) ? go.Name : go.Tag,
                ["dwsimType"] = go.ObjectType.ToString(),
                ["reason"] = "NOT_A_PROCESS_OBJECT",
            });
        }

        // specs on FEEDS only, and results separately
        var streams = new JsonObject();
        var energy = new JsonObject();
        var solved = true;
        var anyStream = false;
        foreach (var o in objects.Select(n => n!.AsObject()).Where(o => (string)o["kind"]! != "unitOp"))
        {
            var tag = (string)o["tag"]!;
            var so = fs.SimulationObjects.Values.First(x => tags[x.Name] == tag);
            if (so is MaterialStream ms)
            {
                anyStream = true;
                var isFeed = !connections.Any(c => (string)c!["to"]! == tag) && !FedByPlaceholder(ms);
                if (isFeed) o["spec"] = FeedSpec(ms, compoundName, warnings, tag);
                else solved &= ms.GraphicObject?.Calculated ?? false;
                streams[tag] = StoredStream(ms, compoundName);
            }
            else if (so is EnergyStream es)
            {
                if (es.EnergyFlow is double kw && double.IsFinite(kw))
                {
                    energy[tag] = new JsonObject { ["kW"] = kw };
                    // An energy stream's duty is a SPECIFICATION only where the unit it feeds is in
                    // `energyStream` mode (it reads the duty off the stream); otherwise it is a result.
                    var consumer = connections.FirstOrDefault(c => (string)c!["from"]! == tag);
                    if (consumer is not null && unitModes.GetValueOrDefault((string)consumer["to"]!) == "energyStream")
                        o["spec"] = new JsonObject { ["duty"] = Quantity(kw, "kW") };
                }
            }
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["name"] = fs.FlowsheetOptions?.SimulationName is { Length: > 0 } n ? n : null,
            ["compounds"] = new JsonArray(compoundName.Values.Select(c => (JsonNode)c!).ToArray()),
            ["propertyPackage"] = documentPackage,
            ["objects"] = objects,
            ["connections"] = connections,
        };
        ReadReactions(fs, tags, compoundName, document, warnings);

        return new JsonObject
        {
            ["document"] = document,
            ["layout"] = layout,
            ["stored"] = new JsonObject { ["solved"] = anyStream && solved, ["streams"] = streams, ["energy"] = energy },
            ["placeholders"] = placeholders,
            ["ignored"] = ignored,
            ["propertyPackages"] = packages,
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w!).ToArray()),
        };

        bool FedByPlaceholder(MaterialStream ms) =>
            ms.GraphicObject?.InputConnectors is { Count: > 0 } ins && ins[0].IsAttached;
    }

    /// <summary>The engine object's gate property holds the value the catalog declares (enum by member name).</summary>
    private static bool GateIsSet(ISimulationObject so, GateDef gate)
    {
        var value = so.GetType().GetProperty(gate.Property)?.GetValue(so);
        return value is not null && (value is Enum e && gate.Value is string member
            ? e.ToString() == member
            : Equals(value, Convert.ChangeType(gate.Value, value.GetType())));
    }

    /// <summary>Why a non-catalog engine type is a placeholder.</summary>
    internal static string PlaceholderReason(ObjectType t) => t switch
    {
        ObjectType.CapeOpenUO => "CAPE_OPEN",
        ObjectType.CustomUO or ObjectType.ExcelUO or ObjectType.Controller_Python => "SCRIPT_BLOCK",
        ObjectType.OT_Adjust or ObjectType.OT_Spec or ObjectType.OT_EnergyRecycle or ObjectType.Controller_PID
            or ObjectType.Switch or ObjectType.Input or ObjectType.AnalogGauge or ObjectType.DigitalGauge
            or ObjectType.LevelGauge => "LOGICAL_BLOCK",
        _ => "NOT_IN_CATALOG",
    };

    /// <summary>
    /// The engine's display name. Measured: a package LOADED from a file has `Name` null (only the
    /// catalog's instances carry it); `ComponentName` (its CAPE-OPEN identity) holds the display name.
    /// </summary>
    private static string PackageName(IPropertyPackage pp) =>
        pp.Name is { Length: > 0 } n ? n
        : pp.GetType().GetProperty("ComponentName")?.GetValue(pp) as string is { Length: > 0 } c ? c
        : pp.Tag ?? pp.GetType().Name;

    private static (string Id, bool Supported) PackageId(IPropertyPackage pp, IReadOnlyCollection<string> engineNames)
    {
        var name = PackageName(pp);
        var id = PackageCatalog.Classify(name).Id;
        // CAPE-OPEN packages are external COM objects, and ISK-530 measured SEAWATER failing every
        // flash headless — both are listed, and neither can be run by build-solve.
        var capeOpen = pp.GetType().Name.Contains("CAPEOPEN", StringComparison.OrdinalIgnoreCase);
        var supported = !capeOpen && id != "SEAWATER" && PackageCatalog.Resolve(id, engineNames) is not null;
        return (id, supported);
    }

    // ── tags ────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> UniqueTags(IFlowsheet fs, List<string> warnings)
    {
        var tags = new Dictionary<string, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var so in fs.SimulationObjects.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            var tag = so.GraphicObject?.Tag is { Length: > 0 } t ? t : so.Name;
            if (!seen.Add(tag))
            {
                var unique = tag;
                for (var i = 2; !seen.Add(unique = $"{tag}#{i}"); i++) { }
                warnings.Add($"two objects are tagged '{tag}'; the second is '{unique}' here");
                tag = unique;
            }
            tags[so.Name] = tag;
        }
        return tags;
    }

    private static string? TagOf(IGraphicObject? go, Dictionary<string, string> tags) =>
        go is not null && tags.TryGetValue(go.Name, out var t) ? t : null;

    // ── ports ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every attached connector of a catalog unit op → a catalog port. Matched by direction and
    /// connector index (the index FlowsheetBuilder connects with), so this is the builder's own
    /// table read backwards. A connector no catalog port claims makes `detail` non-null.
    /// </summary>
    internal static (List<(string Stream, string Port)> Wired, string? Detail) MapPorts(
        IGraphicObject go, UnitOpDef def, Dictionary<string, string> tags)
    {
        var wired = new List<(string, string)>();
        var unclaimed = new List<string>();
        void Claim(string direction, int index, IGraphicObject? other, bool energySlot)
        {
            var stream = TagOf(other, tags);
            if (stream is null) return;
            var port = def.Ports.FirstOrDefault(p => p.Direction == direction && p.Index == index && !energySlot)
                       ?? (energySlot ? def.Ports.SingleOrDefault(p => p.Direction == direction && p.Accepts == "energy") : null);
            if (port is null) unclaimed.Add($"{direction} connector {index} ('{stream}')");
            else wired.Add((stream, port.Name));
        }

        for (var i = 0; i < go.InputConnectors.Count; i++)
            if (go.InputConnectors[i] is { IsAttached: true } cp)
                Claim("in", i, cp.AttachedConnector?.AttachedFrom, false);
        for (var i = 0; i < go.OutputConnectors.Count; i++)
            if (go.OutputConnectors[i] is { IsAttached: true } cp)
                Claim("out", i, cp.AttachedConnector?.AttachedTo, false);
        if (go.EnergyConnector is { IsAttached: true } ec && ec.AttachedConnector is { } link)
        {
            var outgoing = link.AttachedFrom == go;
            Claim(outgoing ? "out" : "in", -1, outgoing ? link.AttachedTo : link.AttachedFrom, true);
        }

        return (wired, unclaimed.Count == 0 ? null
            : $"'{def.Type}' has no catalog port for {string.Join(", ", unclaimed)}");
    }

    private static JsonObject Placeholder(ISimulationObject so, string tag, ObjectType t, string reason, string? detail,
        Dictionary<string, string> tags)
    {
        var ports = new JsonArray();
        var go = so.GraphicObject;
        if (go is not null)
        {
            foreach (var cp in go.InputConnectors.Where(c => c.IsAttached))
                if (TagOf(cp.AttachedConnector?.AttachedFrom, tags) is { } s)
                    ports.Add(new JsonObject { ["stream"] = s, ["direction"] = "in" });
            foreach (var cp in go.OutputConnectors.Where(c => c.IsAttached))
                if (TagOf(cp.AttachedConnector?.AttachedTo, tags) is { } s)
                    ports.Add(new JsonObject { ["stream"] = s, ["direction"] = "out" });
            if (go.EnergyConnector is { IsAttached: true } ec && ec.AttachedConnector is { } link)
            {
                var outgoing = link.AttachedFrom == go;
                if (TagOf(outgoing ? link.AttachedTo : link.AttachedFrom, tags) is { } s)
                    ports.Add(new JsonObject { ["stream"] = s, ["direction"] = outgoing ? "out" : "in" });
            }
        }
        if (so is Column col)   // a column's streams are its own lists, not only its graphic connectors
            foreach (var si in col.MaterialStreams.Values.Concat(col.EnergyStreams.Values))
                if (tags.TryGetValue(si.StreamID ?? "", out var s) && !ports.Any(p => (string)p!["stream"]! == s))
                    ports.Add(new JsonObject
                    {
                        ["stream"] = s,
                        ["direction"] = si.StreamBehavior == SepOps.StreamInformation.Behavior.Feed ? "in" : "out",
                    });

        var node = new JsonObject
        {
            ["tag"] = tag, ["dwsimType"] = t.ToString(), ["reason"] = reason,
            ["ports"] = ports, ["parameters"] = RawParameters(so),
        };
        if (detail is not null) node["detail"] = detail;
        return node;
    }

    /// <summary>Readable scalar properties, raw, through the engine's own property interface.</summary>
    private static JsonObject RawParameters(ISimulationObject so)
    {
        var bag = new JsonObject();
        string[] names;
        try { names = so.GetProperties(DWSIM.Interfaces.Enums.PropertyType.ALL); }
        catch (Exception) { return bag; }
        foreach (var name in names.Distinct().Take(80))
        {
            try
            {
                switch (so.GetPropertyValue(name))
                {
                    case double d when double.IsFinite(d): bag[name] = d; break;
                    case float f when float.IsFinite(f): bag[name] = f; break;
                    case int i: bag[name] = i; break;
                    case bool b: bag[name] = b; break;
                    case string s when s.Length <= 200: bag[name] = s; break;
                }
            }
            catch (Exception) { /* unreadable is simply absent */ }
        }
        return bag;
    }

    // ── unit-op parameters (GP-19: every getter here is exercised by ReaderTests) ───────────

    /// <summary>Catalog parameters whose engine value has no faithful read-back; never emitted.</summary>
    internal static readonly HashSet<(string Type, string Param)> NotReadBack =
    [
        ("componentSeparator", "separationSpecs"),   // per-compound dictionary, bespoke configurator
        ("waterElectrolyzer", "powerInput"),         // consumed into a synthesized energy stream
        // Measured by ReaderTests: `Pipe` has no `Length`/`Diameter` member — the builder refuses
        // both with UNBINDABLE_PARAMETER too. A pipe's geometry lives in its segment profile.
        ("pipe", "length"),
        ("pipe", "diameter"),
    ];

    /// <summary>SI unit per catalog unit type — what the engine stores, and a spelling build-solve accepts.</summary>
    private static readonly Dictionary<string, string> SiUnit = new()
    {
        ["temperature"] = "K", ["temperatureDelta"] = "K", ["pressure"] = "Pa", ["power"] = "kW",
        ["massFlow"] = "kg/s", ["molarFlow"] = "mol/s", ["length"] = "m", ["area"] = "m2",
        ["volume"] = "m3", ["heatTransferCoefficient"] = "W/[m2.K]", ["voltage"] = "V",
    };

    /// <summary>
    /// The unit op's specifications, in the shape `ApplyParameter` takes. With a calculation mode,
    /// only what that mode reads — a heater in outletTemperature mode holds a DeltaQ, and that DeltaQ
    /// is a result.
    /// </summary>
    internal static JsonObject ReadParameters(ISimulationObject so, UnitOpDef def, out string? mode)
    {
        var bag = new JsonObject();
        mode = null;
        if (def.CalcMode is { } cm && so.GetType().GetProperty(cm.ClrProperty)?.GetValue(so) is { } member)
        {
            var wire = UnitOpCatalog.NormalizeMode(member.ToString()!);
            mode = cm.AliasOf(wire) ?? wire;
            bag["calcMode"] = mode;
        }

        foreach (var p in def.Parameters)
        {
            if (p.Name == "calcMode" || p.EngineProperties.Length == 0 || NotReadBack.Contains((def.Type, p.Name))) continue;
            if (mode is not null && def.CalcMode is { } c && !c.Reads(mode, p.Name)) continue;
            // #31 review 3 — a gated value is a specification only when the file set its gates; with
            // a gate off the engine holds a default it does not read (valve.openingPct = 50).
            if (p.Gates is { Length: > 0 } gates && !gates.All(g => GateIsSet(so, g))) continue;

            object? raw;
            if (def.Type == "splitter" && p.Name == "splitRatio1")
                raw = so.GetType().GetProperty("Ratios")?.GetValue(so) is System.Collections.IList { Count: > 0 } r ? r[0] : null;
            else
                raw = GetEngineValue(so, p.EngineProperties);
            if (raw is null) continue;

            if (raw is string s) { bag[p.Name] = s; continue; }
            double v;
            try { v = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { continue; }
            if (!double.IsFinite(v)) continue;

            // Heater/cooler efficiency is a PERCENT on the engine (m_eta, default 100) and a fraction
            // in the document — ApplyParameter multiplies by 100, so this divides.
            if (def.Type is "heater" or "cooler" && p.Name == "efficiency") v /= 100.0;

            bag[p.Name] = p.UnitType switch
            {
                "integer" => (int)Math.Round(v),
                _ when SiUnit.TryGetValue(p.UnitType, out var unit) => Quantity(v, unit),
                _ => v,
            };
        }
        return bag;
    }

    private static object? GetEngineValue(ISimulationObject so, string[] candidates)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        foreach (var name in candidates)
        {
            if (so.GetType().GetProperty(name, Flags) is { CanRead: true } prop) return prop.GetValue(so);
            if (so.GetType().GetField(name, Flags) is { } field) return field.GetValue(so);
        }
        return null;
    }

    private static JsonObject Quantity(double value, string unit) => new() { ["value"] = value, ["unit"] = unit };

    // ── the rigorous column: its own connection lists and specs (ColumnConfigurator backwards) ──

    private static readonly Dictionary<string, string> ColumnMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wang-Henke (Bubble Point)"] = "wangHenke",
        ["Modified Wang-Henke (Bubble Point)"] = "modifiedWangHenke",
        ["Napthali-Sandholm (Simultaneous Correction)"] = "naphtaliSandholm",
        ["Burningham-Otto (Sum Rates)"] = "burninghamOtto",
    };

    internal static (JsonObject Parameters, List<(string Stream, string Port)> Wired, string? Detail) ReadColumn(
        DistillationColumn col, Dictionary<string, string> tags)
    {
        var bag = new JsonObject();
        var wired = new List<(string, string)>();
        var problems = new List<string>();
        // AssociatedStage holds the stage's NAME (Column.ConnectFeed sets it from Stages[i].Name —
        // read in the IL after the first round trip lost feedStage); IDs are kept as a fallback.
        var stageIndex = new Dictionary<string, int>();
        for (var i = col.Stages.Count - 1; i >= 0; i--)
        {
            if (col.Stages[i].Name is { Length: > 0 } sn) stageIndex[sn] = i;
            if (col.Stages[i].ID is { Length: > 0 } sid) stageIndex.TryAdd(sid, i);
        }

        foreach (var si in col.MaterialStreams.Values)
        {
            if (!tags.TryGetValue(si.StreamID ?? "", out var stream)) continue;
            switch (si.StreamBehavior)
            {
                case SepOps.StreamInformation.Behavior.Feed when !wired.Any(w => w.Item2 == "Feed"):
                    wired.Add((stream, "Feed"));
                    if (stageIndex.TryGetValue(si.AssociatedStage ?? "", out var idx)) bag["feedStage"] = idx + 1;
                    break;
                case SepOps.StreamInformation.Behavior.Distillate or SepOps.StreamInformation.Behavior.OverheadVapor
                    when !wired.Any(w => w.Item2 == "Distillate"):
                    wired.Add((stream, "Distillate"));
                    break;
                case SepOps.StreamInformation.Behavior.BottomsLiquid:
                    wired.Add((stream, "Bottoms"));
                    break;
                default:
                    problems.Add($"{si.StreamBehavior} stream '{stream}'");
                    break;
            }
        }
        foreach (var si in col.EnergyStreams.Values)
        {
            if (!tags.TryGetValue(si.StreamID ?? "", out var stream)) continue;
            // The condenser duty leaves the column; the reboiler duty enters it. The behaviour
            // decides first, then the stage the duty is on.
            var stage = stageIndex.TryGetValue(si.AssociatedStage ?? "", out var at) ? at : -1;
            var port = si.StreamBehavior switch
            {
                SepOps.StreamInformation.Behavior.Distillate => "Condenser Duty",
                SepOps.StreamInformation.Behavior.BottomsLiquid => "Reboiler Duty",
                _ => stage == 0 ? "Condenser Duty" : stage == col.Stages.Count - 1 ? "Reboiler Duty" : null,
            };
            if (port is null) problems.Add($"energy stream '{stream}' ({si.StreamBehavior}, stage '{si.AssociatedStage}') is neither the condenser nor the reboiler duty");
            else wired.Add((stream, port));
        }

        bag["numberOfStages"] = col.Stages.Count;
        if (col.Specs.TryGetValue("C", out var cond))
        {
            if (cond.SType == SepOps.ColumnSpec.SpecType.Stream_Ratio) bag["refluxRatio"] = cond.SpecValue;
            else if (cond.SType == SepOps.ColumnSpec.SpecType.Product_Molar_Flow_Rate)
                bag["distillateMolarFlow"] = Quantity(cond.SpecValue, cond.SpecUnit is { Length: > 0 } u ? u : "mol/s");
            else problems.Add($"condenser spec {cond.SType}");
        }
        if (col.Specs.TryGetValue("R", out var reb))
        {
            if (reb.SType == SepOps.ColumnSpec.SpecType.Product_Molar_Flow_Rate)
                bag["bottomsMolarFlow"] = Quantity(reb.SpecValue, reb.SpecUnit is { Length: > 0 } u ? u : "mol/s");
            else problems.Add($"reboiler spec {reb.SType}");
        }
        bag["condenserPressure"] = Quantity(col.Stages[0].P, "Pa");
        bag["reboilerPressure"] = Quantity(col.Stages[^1].P, "Pa");
        if (ColumnMethods.TryGetValue(col.SolvingMethodName ?? "", out var method)) bag["solvingMethod"] = method;
        bag["maxIterations"] = col.MaxIterations;

        return (bag, wired, problems.Count == 0 ? null
            : "the rigorous column holds what the catalog cannot state: " + string.Join("; ", problems));
    }

    // ── streams ─────────────────────────────────────────────────────────────

    private static JsonObject FeedSpec(MaterialStream ms, Dictionary<string, string> compoundName, List<string> warnings, string tag)
    {
        // Only what the file SPECIFIES: a value the engine computed (a PH feed's temperature) would
        // be stamped a design input downstream. Pressure is specified by every StreamSpec except the
        // volume ones; temperature only by T-P and T-VF.
        var props = ms.Phases[0].Properties;
        var spec = new JsonObject();
        var specType = ms.SpecType;
        var tSpecified = specType is DWSIM.Interfaces.Enums.StreamSpec.Temperature_and_Pressure
            or DWSIM.Interfaces.Enums.StreamSpec.Temperature_and_VaporFraction;
        var pSpecified = specType is not (DWSIM.Interfaces.Enums.StreamSpec.Temperature_and_VaporFraction
            or DWSIM.Interfaces.Enums.StreamSpec.Volume_and_Temperature or DWSIM.Interfaces.Enums.StreamSpec.Volume_and_Enthalpy
            or DWSIM.Interfaces.Enums.StreamSpec.Volume_and_Entropy);
        if (tSpecified && props.temperature is double t && double.IsFinite(t)) spec["temperature"] = Quantity(t, "K");
        if (pSpecified && props.pressure is double p && double.IsFinite(p)) spec["pressure"] = Quantity(p, "Pa");
        if (ms.DefinedFlow == DWSIM.Interfaces.Enums.FlowSpec.Mole && props.molarflow is double n && double.IsFinite(n))
            spec["molarFlow"] = Quantity(n, "mol/s");
        else if (ms.DefinedFlow == DWSIM.Interfaces.Enums.FlowSpec.Mass && props.massflow is double m && double.IsFinite(m))
            spec["massFlow"] = Quantity(m, "kg/s");

        var mass = ms.CompositionBasis == DWSIM.Interfaces.Enums.CompositionBasis.Mass_Fractions;
        var fractions = new JsonObject();
        foreach (var c in ms.Phases[0].Compounds.Values)
        {
            var f = mass ? c.MassFraction : c.MoleFraction;
            if (f is double x && double.IsFinite(x)) fractions[compoundName.GetValueOrDefault(c.Name, c.Name)] = x;
        }
        spec["composition"] = new JsonObject { ["basis"] = mass ? "mass" : "molar", ["fractions"] = fractions };

        if (ms.SpecType != DWSIM.Interfaces.Enums.StreamSpec.Temperature_and_Pressure)
            warnings.Add($"feed '{tag}' is specified by {ms.SpecType} in the file; the document carries only the " +
                         "variables of that pair it can state, so this feed is under-specified (its stored state is in `stored`)");
        if (ms.DefinedFlow == DWSIM.Interfaces.Enums.FlowSpec.Volumetric)
            warnings.Add($"feed '{tag}' is specified by volumetric flow in the file, which a document cannot state; its flow is omitted (the stored mass flow is in `stored`)");
        return spec;
    }

    private static JsonObject StoredStream(MaterialStream ms, Dictionary<string, string> compoundName)
    {
        var props = ms.Phases[0].Properties;
        var row = new JsonObject();
        void Put(string key, double? v) { if (v is double d && double.IsFinite(d)) row[key] = d; }
        Put("T_K", props.temperature);
        Put("P_Pa", props.pressure);
        Put("massFlow_kg_s", props.massflow);
        Put("molarFlow_mol_s", props.molarflow);
        var x = new JsonObject();
        foreach (var c in ms.Phases[0].Compounds.Values)
            if (c.MoleFraction is double f && double.IsFinite(f))
                x[compoundName.GetValueOrDefault(c.Name, c.Name)] = f;
        row["x"] = x;
        return row;
    }

    // ── reactions (FlowsheetBuilder.BuildReactions backwards) ────────────────

    private static void ReadReactions(IFlowsheet fs, Dictionary<string, string> tags,
        Dictionary<string, string> compoundName, JsonObject document, List<string> warnings)
    {
        if (fs.Reactions.Count == 0) return;
        var reactions = new JsonArray();
        var names = new Dictionary<string, string>();   // reaction id → document tag
        foreach (var rx in fs.Reactions.Values)
        {
            var type = rx.ReactionType switch
            {
                DWSIM.Interfaces.Enums.ReactionType.Conversion => "conversion",
                DWSIM.Interfaces.Enums.ReactionType.Equilibrium => "equilibrium",
                DWSIM.Interfaces.Enums.ReactionType.Kinetic => "kinetic",
                DWSIM.Interfaces.Enums.ReactionType.Heterogeneous_Catalytic => "heterogeneousCatalytic",
                _ => null,
            };
            if (type is null) { warnings.Add($"reaction '{rx.Name}' has type {rx.ReactionType}, which a document cannot state"); continue; }
            var tag = rx.Name is { Length: > 0 } n ? n : rx.ID;
            names[rx.ID] = tag;
            string C(string name) => compoundName.GetValueOrDefault(name, name);
            var node = new JsonObject
            {
                ["tag"] = tag, ["type"] = type,
                // The builder's basis vocabulary, not the enum's: "MassFrac" would fall through its
                // switch to molar fractions.
                ["basis"] = rx.ReactionBasis switch
                {
                    DWSIM.Interfaces.Enums.ReactionBasis.MassFrac => "mass",
                    DWSIM.Interfaces.Enums.ReactionBasis.PartialPress => "partialPressure",
                    DWSIM.Interfaces.Enums.ReactionBasis.MolarConc => "molarConcentration",
                    DWSIM.Interfaces.Enums.ReactionBasis.Fugacity => "fugacity",
                    DWSIM.Interfaces.Enums.ReactionBasis.Activity => "activity",
                    _ => "Molar Fractions",
                },
                ["phase"] = rx.ReactionPhase.ToString(),
                ["baseCompound"] = C(rx.BaseReactant),
                ["stoichiometry"] = new JsonObject(rx.Components.Values.Select(s =>
                    KeyValuePair.Create(C(s.CompName), (JsonNode?)s.StoichCoeff))),
            };
            switch (type)
            {
                case "conversion":
                    node["conversionExpression"] = rx.Expression;
                    break;
                case "equilibrium":
                    node["equilibriumConstantSource"] = rx.KExprType switch
                    {
                        DWSIM.Interfaces.Enums.KOpt.Constant => rx.ConstantKeqValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        DWSIM.Interfaces.Enums.KOpt.Gibbs => "Gibbs Energy",
                        _ => rx.Expression,
                    };
                    break;
                case "kinetic":
                    node["a"] = rx.A_Forward;
                    node["e"] = rx.E_Forward;
                    node["directOrders"] = new JsonObject(rx.Components.Values.Select(s =>
                        KeyValuePair.Create(C(s.CompName), (JsonNode?)s.DirectOrder)));
                    node["reverseOrders"] = new JsonObject(rx.Components.Values.Select(s =>
                        KeyValuePair.Create(C(s.CompName), (JsonNode?)s.ReverseOrder)));
                    if (rx.A_Reverse != 0) warnings.Add($"reaction '{tag}' has a reverse rate the document cannot state");
                    break;
            }
            reactions.Add(node);
        }

        var sets = new JsonArray();
        foreach (var set in fs.ReactionSets.Values)
        {
            var members = set.Reactions.Values.Where(r => r.IsActive).OrderBy(r => r.Rank)
                .Select(r => names.GetValueOrDefault(r.ReactionID)).OfType<string>().ToList();
            var attach = fs.SimulationObjects.Values
                .Where(o => o.GetType().GetProperty("ReactionSetID")?.GetValue(o) as string == set.ID)
                .Select(o => tags[o.Name]).ToList();
            if (members.Count == 0 && attach.Count == 0) continue;
            sets.Add(new JsonObject
            {
                ["tag"] = set.Name is { Length: > 0 } n ? n : set.ID,
                ["reactions"] = new JsonArray(members.Select(m => (JsonNode)m!).ToArray()),
                ["attachTo"] = new JsonArray(attach.Select(a => (JsonNode)a!).ToArray()),
            });
        }
        document["reactions"] = reactions;
        document["reactionSets"] = sets;
    }

    private static T? SafeGet<T>(Func<T> get) where T : class
    {
        try { return get(); } catch (Exception) { return null; }
    }
}
