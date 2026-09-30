// dwsim-runner Worker — GPL-3.0
// Engine lookups every mode needs, written ONCE (ISK-442). Each of these used to be written two or
// three times across Program.cs, Modes.cs and FlowsheetBuilder.cs, and copies drift: a compound
// suggestion, a package fallback or a unit conversion that behaves differently depending on which
// entry point ran is the defect class this repo keeps paying for (Hazard 7).

using DWSIM.Automation;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;

namespace DwsimRunner.Worker;

internal static class Engine
{
    // ── templates ───────────────────────────────────────────────────────────
    /// <summary>Load a `.dwxmz` template; any engine failure is a TemplateLoadException (exit 3).</summary>
    public static IFlowsheet LoadTemplate(Automation3 auto, string template)
    {
        object? fsObj;
        try { fsObj = auto.LoadFlowsheet(template); }
        catch (Exception ex) { throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}': {ex.Message}"); }
        return (fsObj as IFlowsheet)
               ?? throw new TemplateLoadException($"failed to load '{Path.GetFileName(template)}'");
    }

    // ── property packages ───────────────────────────────────────────────────
    /// <summary>The engine display names of every property package a flowsheet can create.</summary>
    public static List<string> PropertyPackageNames(IFlowsheet fs) =>
        fs.GetAvailablePropertyPackages().Cast<string>()
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

    /// <summary>
    /// Package names without a document flowsheet. `Automation3.AvailablePropertyPackages` stays
    /// empty headless, so this falls back to a scratch flowsheet's listing — and hands that
    /// flowsheet back (null when none was needed) so a caller that needs one anyway can reuse it.
    /// </summary>
    public static (List<string> Names, IFlowsheet? Scratch) PropertyPackageNames(Automation3 auto)
    {
        var names = ((System.Collections.IEnumerable)auto.AvailablePropertyPackages.Values)
            .Cast<IPropertyPackage>().Select(pp => pp.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count > 0) return (names, null);
        var scratch = auto.CreateFlowsheet();
        return (scratch is null ? names : PropertyPackageNames(scratch), scratch);
    }

    /// <summary>The refusal for a package id nothing resolves, listing the ids that would.</summary>
    public static string UnknownPackageMessage(string? requested, IEnumerable<string> engineNames) =>
        $"property package '{requested}' not found; available ids: " +
        string.Join(", ", engineNames.Select(n => PackageCatalog.Classify(n).Id).Distinct().Order());

    // ── compounds ───────────────────────────────────────────────────────────
    /// <summary>
    /// Resolve a requested compound to the engine's own spelling, case-insensitively. Returns null
    /// when nothing matches, with <paramref name="notFound"/> carrying the refusal and up to five
    /// suggestions.
    /// </summary>
    public static string? ResolveCompound(IEnumerable<string> available, string requested, out string notFound)
    {
        var names = available as ICollection<string> ?? available.ToList();
        var match = names.FirstOrDefault(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            notFound = "";
            return match;
        }
        var suggestions = names
            .Where(k => k.Contains(requested, StringComparison.OrdinalIgnoreCase)
                     || requested.Length >= 4 && k.StartsWith(requested[..4], StringComparison.OrdinalIgnoreCase))
            .Take(5).ToList();
        notFound = $"compound '{requested}' not found" +
                   (suggestions.Count > 0 ? $"; did you mean: {string.Join(", ", suggestions)}?" : "");
        return null;
    }

    // ── units ───────────────────────────────────────────────────────────────
    /// <summary>
    /// A quantity in the engine's SI, via the engine's own converter; a bare number is taken as SI.
    /// </summary>
    /// <remarks>
    /// There is deliberately no "expected SI unit" parameter. The two copies this replaced both took
    /// one and both ignored it — and one caller labelled a pressure "bar" while the value reaching
    /// the engine is Pa, so the label was not even right. The TARGET unit is the converter's choice
    /// (DWSIM's SI store), not the caller's. What the caller can get wrong is the DIMENSION — a
    /// temperature unit on a pressure — and that is refused before the worker runs, per quantity,
    /// by the API's `DocumentValidator.UnitRefusal`: the converter returns a value UNCHANGED for a
    /// spelling it does not know, so the check has to happen where the vocabulary lives.
    /// </remarks>
    public static double ToSi(FlowQuantity q) =>
        q.Unit is { Length: > 0 }
            ? DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI(q.Unit, q.Value)
            : q.Value;

    // ── wire types ──────────────────────────────────────────────────────────
    /// <summary>
    /// Stable, engine-agnostic type name for a simulation object crossing the HTTP boundary (FR-014).
    /// </summary>
    /// <remarks>
    /// DERIVED FROM `UnitOpCatalog.Types`, never listed. The two hand-written copies this replaced
    /// reported all five reactor types as "reactor", although the catalog already names them
    /// `reactorConversion`/`reactorEquilibrium`/`reactorGibbs`/`reactorCSTR`/`reactorPFR` — so a
    /// solve reported a type no document can carry, and a caller could not join a result row back to
    /// the type it asked for. The engine type is read off the graphic object (the object's own
    /// `ObjectType`); failing that, off the CLR class name when it IS an `ObjectType` member. Only
    /// a type the catalog does not expose falls back to the camel-cased class name.
    /// </remarks>
    public static string FriendlyType(object obj) => obj switch
    {
        DWSIM.Thermodynamics.Streams.MaterialStream => "materialStream",
        DWSIM.UnitOperations.Streams.EnergyStream => "energyStream",
        _ => FriendlyType((obj as ISimulationObject)?.GraphicObject?.ObjectType, obj.GetType().Name),
    };

    /// <summary>The pure half of <see cref="FriendlyType(object)"/>, separable for tests.</summary>
    public static string FriendlyType(ObjectType? engineType, string clrTypeName)
    {
        if (engineType is { } t && UnitOpCatalog.WireTypeFor(t) is { } wire) return wire;
        if (Enum.TryParse<ObjectType>(clrTypeName, ignoreCase: false, out var byName)
            && UnitOpCatalog.WireTypeFor(byName) is { } wireByName) return wireByName;
        return clrTypeName.Length == 0 ? clrTypeName : char.ToLowerInvariant(clrTypeName[0]) + clrTypeName[1..];
    }
}
