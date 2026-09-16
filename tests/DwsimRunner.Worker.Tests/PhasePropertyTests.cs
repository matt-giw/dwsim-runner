// Spec 259 (ISK-338) — the property bag's two load-bearing properties, against the REAL
// DWSIM interface.
//
// The bag is the one place in this codebase where a number's unit is not spelled in its field name.
// That is a deliberate trade (see PhaseProperties.cs), and it is only safe while two things hold:
//
//   1. EVERY member the engine declares has a declared unit. A member with no unit is refused by
//      the harvest, so the failure mode is a silently missing property rather than an unlabelled
//      one — quieter, but still a gap, and nobody would notice it without this test.
//   2. The unit table only ever names members that EXIST. A typo'd key is dead weight that looks
//      like coverage; spec 200 bound `head` to a property the engine never reads and measured it
//      as working, which is this failure with the arrow reversed.
//
// Both are assertions about the engine, so they run against the real `IPhaseProperties` rather than
// a fixture. Spec 199's lesson: a hand-built denominator can be neither satisfied nor falsified.

using DWSIM.Interfaces;
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class PhasePropertyTests
{
    private static string[] EngineMembers() =>
        typeof(IPhaseProperties)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(double?) && p.CanRead)
            .Select(p => p.Name)
            .ToArray();

    [Fact]
    public void every_engine_member_has_a_declared_unit()
    {
        // Non-empty means a DWSIM upgrade moved the denominator. That is not a warning to live
        // with: the named members would keep working while the new ones silently never appear, and
        // the only symptom is a property an engineer expected to see and did not.
        Assert.True(
            PhaseProperties.UnmappedMembers.Count == 0,
            $"IPhaseProperties declares {PhaseProperties.UnmappedMembers.Count} member(s) with no unit in " +
            $"PhaseProperties.SiUnits, so they are NOT harvested: {string.Join(", ", PhaseProperties.UnmappedMembers)}. " +
            "Add each to SiUnits with its SI unit — sourced from DWSIM.SharedClasses.SystemsOfUnits.SI where that " +
            "class names it, and by stated analogy where it does not. Do not delete this assertion.");
    }

    [Fact]
    public void the_unit_table_names_no_member_the_engine_does_not_have()
    {
        var engine = EngineMembers().ToHashSet(StringComparer.Ordinal);
        var phantom = PhaseProperties.SiUnits.Keys.Where(k => !engine.Contains(k)).OrderBy(k => k).ToArray();

        // A key here that the engine does not declare can never be harvested, so it reads as
        // coverage while contributing nothing. `mean_ionic_acitivty_coefficient` is DWSIM's own
        // misspelling and MUST be present — correcting it would land it in this list.
        Assert.True(
            phantom.Length == 0,
            $"PhaseProperties.SiUnits names {phantom.Length} member(s) IPhaseProperties does not declare: " +
            $"{string.Join(", ", phantom)}. Check the spelling against the engine, not against what reads right.");
    }

    [Fact]
    public void the_engine_declares_only_nullable_scalars_here()
    {
        // The whole design rests on this: no arrays, no per-compound vectors, so the harvest is a
        // loop rather than 63 decisions. If DWSIM ever adds a vector member, `Harvest` would need a
        // shape this bag cannot express, and that must surface as a failing test rather than as a
        // cast exception in a solve.
        var notScalar = typeof(IPhaseProperties)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.PropertyType != typeof(double?))
            .Select(p => $"{p.Name}: {p.PropertyType.Name}")
            .ToArray();

        Assert.True(
            notScalar.Length == 0,
            $"IPhaseProperties has non-scalar member(s): {string.Join(", ", notScalar)}. The property bag " +
            "carries doubles only — decide deliberately what to do with these rather than letting Harvest skip them.");
    }

    [Fact]
    public void units_for_response_covers_exactly_what_can_be_harvested()
    {
        // FR-002/FR-003 as one statement: the declared units and the harvestable set are the same
        // set. If the response could carry a key the units map does not name, the app's fold would
        // drop it as unlabelled — correct, but a payload spent for nothing.
        var declared = PhaseProperties.UnitsForResponse().Keys.ToHashSet(StringComparer.Ordinal);
        var harvestable = EngineMembers().Where(PhaseProperties.SiUnits.ContainsKey).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(harvestable.OrderBy(x => x, StringComparer.Ordinal), declared.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void a_null_properties_object_harvests_nothing_rather_than_throwing()
    {
        Assert.Null(PhaseProperties.Harvest(null));
    }

    [Fact]
    public void units_come_from_the_engine_not_from_what_the_name_suggests()
    {
        // Three the engine's own SI table settles against intuition. `activity` looks
        // dimensionless and is Pa; `compressibility` looks dimensionless and is 1/Pa; `molarflow`
        // is mol/s, which is why the named MolarFlowKmolH field scales by 3.6 and not by 1000.
        Assert.Equal("Pa", PhaseProperties.SiUnits["activity"]);
        Assert.Equal("1/Pa", PhaseProperties.SiUnits["compressibility"]);
        Assert.Equal("mol/s", PhaseProperties.SiUnits["molarflow"]);
        Assert.Equal("kJ/kg", PhaseProperties.SiUnits["enthalpy"]);
        Assert.Equal("kJ/kmol", PhaseProperties.SiUnits["molar_enthalpy"]);
    }
}
