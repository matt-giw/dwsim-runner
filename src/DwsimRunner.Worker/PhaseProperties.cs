// dwsim-runner Worker — GPL-3.0
//
// Spec 259 (ISK-338) — every property DWSIM computed for a phase, in the engine's own SI store.
//
// WHY A BAG AND NOT SIXTY-THREE NAMED FIELDS
//
// `IPhaseProperties` declares 63 members and every one is a `Nullable<float64>` — no arrays, no
// per-compound vectors (enumerated with ikdasm; see specs/259-.../contracts/phase-properties.md).
// The harvest could therefore name all 63 with unit suffixes the way `DensityKgM3` and
// `HeatCapacityKJKgK` are named. It deliberately does not.
//
// Every unit bug this repo has recorded came from a CONVERSION, and each was self-consistent and
// silent: enthalpy divided by 1000 reporting MJ/kg under a kJ/kg name (Modes.cs), entropy rounded
// to zero by that same divide, pressure at three decimals of bar destroying the 25 Pa in one
// atmosphere, a temperature DELTA converted as an absolute temperature (spec 200), a pump
// efficiency crossing the wire as 0.75 PERCENT (spec 251). Sixty-three hand-converted fields is
// sixty-three chances to write that bug again. This converts NOTHING: the value is whatever DWSIM
// stores, and the unit is stated separately.
//
// The existing named fields are untouched. They are the curated, converted, contracted set that
// callers already read; this is additive beside them, and `OpenApiContractTests` asserts the two
// agree for every quantity that appears in both.
//
// THE UNIT TABLE IS THE GATE
//
// A member with no unit in `SiUnits` is NOT harvested (FR-002). An unlabelled number is exactly the
// wrong-unit bug this design exists to avoid, and refusing it costs less than shipping it.
//
// Units come from DWSIM's OWN `DWSIM.SharedClasses.SystemsOfUnits.SI` wherever that class names the
// member — read out of the assembly rather than from memory, which is how `activity` is here as
// **Pa** and not the dimensionless it looks like. Where `SI` has no entry the unit is stated by
// explicit analogy to a member it DOES name, and the analogy is written next to it. Neither kind is
// a guess left implicit.
//
// A NAME IN `SI` IS NOT THE NAME ON `IPhaseProperties`. They disagree in spelling and case for
// several members — `volumetric_flow`/`volumetricFlow`, `kinematic_viscosity`/`cinematic_viscosity`,
// `compressibilityFactor`/`compressibilityfactor`, `logFugacityCoefficient`/`logfugacityCoefficient`.
// Binding by "the name looks right" is how spec 200 bound `head` to `PolytropicHead`, a property the
// engine never reads, and measured it as working. The key here is ALWAYS the `IPhaseProperties`
// member name; the `SI` spelling appears only in the comment that sourced the unit.

using System.Reflection;
using DWSIM.Interfaces;

namespace DwsimRunner.Worker;

internal static class PhaseProperties
{
    /// <summary>
    /// SI unit per `IPhaseProperties` member. Keys are the member names verbatim — including
    /// DWSIM's misspelling of `mean_ionic_acitivty_coefficient`, which is carried rather than
    /// corrected so the bag and the engine cannot disagree about what a key is.
    ///
    /// `[SI]` = named by `SystemsOfUnits.SI`. `[~x]` = not named there; same dimension as member x.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SiUnits = new Dictionary<string, string>
    {
        // ── named directly by SystemsOfUnits.SI ──────────────────────────────────────────
        ["temperature"] = "K",                       // [SI]
        ["pressure"] = "Pa",                         // [SI]
        ["density"] = "kg/m3",                       // [SI]
        ["massflow"] = "kg/s",                       // [SI]
        ["molarflow"] = "mol/s",                     // [SI]
        ["massfraction"] = "-",                      // [SI]
        ["molarfraction"] = "-",                     // [SI]
        ["molecularWeight"] = "kg/kmol",             // [SI]
        ["enthalpy"] = "kJ/kg",                      // [SI]
        ["entropy"] = "kJ/[kg.K]",                   // [SI]
        ["excessEnthalpy"] = "kJ/kg",                // [SI]
        ["excessEntropy"] = "kJ/[kg.K]",             // [SI]
        ["molar_enthalpy"] = "kJ/kmol",              // [SI]
        ["molar_entropy"] = "kJ/[kmol.K]",           // [SI]
        ["heatCapacityCp"] = "kJ/[kg.K]",            // [SI]
        ["heatCapacityCv"] = "kJ/[kg.K]",            // [SI]
        ["thermalConductivity"] = "W/[m.K]",         // [SI]
        ["viscosity"] = "Pa.s",                      // [SI]
        ["kinematic_viscosity"] = "m2/s",            // [SI] as `cinematic_viscosity` (DWSIM's spelling)
        ["surfaceTension"] = "N/m",                  // [SI]
        ["speedOfSound"] = "m/s",                    // [SI]
        ["compressibility"] = "1/Pa",                // [SI]
        ["compressibilityFactor"] = "-",             // [SI] as `compressibilityfactor`
        ["jouleThomsonCoefficient"] = "K/Pa",        // [SI]
        ["volumetric_flow"] = "m3/s",                // [SI] as `volumetricFlow`
        ["idealGasHeatCapacityCp"] = "kJ/[kg.K]",    // [SI] as `idealGasHeatCapacity`
        ["activity"] = "Pa",                         // [SI] — Pa, NOT dimensionless. Measured, not assumed.
        ["activityCoefficient"] = "-",               // [SI]
        ["fugacity"] = "Pa",                         // [SI]
        ["fugacityCoefficient"] = "-",               // [SI]
        ["logFugacityCoefficient"] = "-",            // [SI] as `logfugacityCoefficient`
        ["kvalue"] = "-",                            // [SI]
        ["logKvalue"] = "-",                         // [SI]

        // ── not named by SystemsOfUnits.SI; dimension stated by analogy ──────────────────
        // The "F" variants are the formation-basis counterparts: same dimension as the plain one.
        ["enthalpyF"] = "kJ/kg",                     // [~enthalpy]
        ["entropyF"] = "kJ/[kg.K]",                  // [~entropy]
        ["molar_enthalpyF"] = "kJ/kmol",             // [~molar_enthalpy]
        ["molar_entropyF"] = "kJ/[kmol.K]",          // [~molar_entropy]
        // Specific energies follow enthalpy; their molar twins follow molar_enthalpy.
        ["internal_energy"] = "kJ/kg",               // [~enthalpy]
        ["gibbs_free_energy"] = "kJ/kg",             // [~enthalpy]
        ["helmholtz_energy"] = "kJ/kg",              // [~enthalpy]
        ["molar_internal_energy"] = "kJ/kmol",       // [~molar_enthalpy]
        ["molar_gibbs_free_energy"] = "kJ/kmol",     // [~molar_enthalpy]
        ["molar_helmholtz_energy"] = "kJ/kmol",      // [~molar_enthalpy]
        ["isothermal_compressibility"] = "1/Pa",     // [~compressibility]
        ["bulk_modulus"] = "Pa",                     // [~pressure] — the reciprocal of a compressibility
        ["idealGasHeatCapacityRatio"] = "-",         // Cp/Cv, dimensionless by construction
        ["volumetricFraction"] = "-",                // [~molarfraction]
        // Saturation points are temperatures and pressures.
        ["bubbleTemperature"] = "K",                 // [~temperature]
        ["dewTemperature"] = "K",                    // [~temperature]
        ["freezingPoint"] = "K",                     // [~temperature]
        ["bubblePressure"] = "Pa",                   // [~pressure]
        ["dewPressure"] = "Pa",                      // [~pressure]
        // A DEPRESSION is a temperature DIFFERENCE. K and degC are the same interval; the point of
        // saying so here is that nothing downstream may convert it as an absolute temperature —
        // `ConvertToSI("C", 10)` is 283.15, which is spec 200's `temperatureDelta` finding.
        ["freezingPointDepression"] = "K",           // [~temperature], but an INTERVAL
        // Electrolyte and solids members. Null on an ordinary hydrocarbon or steam flowsheet, so
        // they cost nothing on the wire — but declared, so a sour-gas or crystalliser document that
        // does populate them is not silently dropped for want of a row here.
        ["CO2partialpressure"] = "Pa",               // [~pressure]
        ["H2Spartialpressure"] = "Pa",               // [~pressure]
        ["CO2loading"] = "mol/mol",                  // loading is moles of solute per mole of solvent
        ["H2Sloading"] = "mol/mol",
        ["pH"] = "-",
        ["ionicStrength"] = "mol/kg",                // molal basis, as DWSIM's electrolyte package works in
        ["osmoticCoefficient"] = "-",
        ["mean_ionic_acitivty_coefficient"] = "-",   // sic — DWSIM's spelling, carried verbatim
        ["particleSize_Mean"] = "m",                 // [~distance]
        ["particleSize_StdDev"] = "m",               // [~distance]
    };

    /// <summary>
    /// Every `IPhaseProperties` getter that returns `double?`, resolved once.
    ///
    /// Reflected rather than listed, so the harvest cannot silently fall behind a DWSIM upgrade that
    /// adds a member: an unknown member simply has no unit and is refused by name in
    /// <see cref="UnmappedMembers"/>, which a test asserts is empty. Spec 199's lesson — a hand-built
    /// denominator can be neither satisfied nor falsified.
    /// </summary>
    private static readonly PropertyInfo[] Members = typeof(IPhaseProperties)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(double?) && p.CanRead)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Members the engine declares that <see cref="SiUnits"/> does not name. Expected
    /// EMPTY; a non-empty list is a DWSIM upgrade that moved the denominator, not a warning to
    /// live with. Asserted by PhasePropertyTests.</summary>
    internal static IReadOnlyList<string> UnmappedMembers =>
        Members.Select(m => m.Name).Where(n => !SiUnits.ContainsKey(n)).ToArray();

    /// <summary>The unit table as the response carries it — ONCE per response, never per row
    /// (FR-003). Restricted to members that actually exist on this engine's interface, so the
    /// declared units and the harvestable set cannot disagree.</summary>
    internal static Dictionary<string, string> UnitsForResponse() =>
        Members.Where(m => SiUnits.ContainsKey(m.Name))
               .ToDictionary(m => m.Name, m => SiUnits[m.Name], StringComparer.Ordinal);

    /// <summary>
    /// Everything the engine populated for one phase, in SI, unconverted.
    ///
    /// Null and non-finite are both "not reported" and are omitted — `NaN` is what an unconverged
    /// property leaves behind, and a NaN crossing the wire as a number is a wrong answer that
    /// survives every type check. Returns null rather than an empty map so the field disappears
    /// entirely under the worker's `WhenWritingNull` serializer.
    /// </summary>
    internal static Dictionary<string, double>? Harvest(IPhaseProperties? props)
    {
        if (props is null) return null;
        var bag = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var m in Members)
        {
            if (!SiUnits.ContainsKey(m.Name)) continue;   // FR-002 — no declared unit, no harvest
            double? v;
            try { v = (double?)m.GetValue(props); }
            catch { continue; }                           // a getter that throws is not a result
            if (v is double d && double.IsFinite(d)) bag[m.Name] = d;
        }
        return bag.Count > 0 ? bag : null;
    }
}
