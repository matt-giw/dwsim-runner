// dwsim-runner Worker tests — GPL-3.0
// Spec 199 — the calculation mode is an input.
//
// These gates are written against the CATALOG PAYLOAD rather than against the C# types, so they
// fail on missing DATA rather than on a missing symbol. A compile error is a weak red: it proves
// the type is absent, not that the behaviour is. Every assertion here fails on the pre-199 catalog
// for a reason the message states.
//
// The denominator is `specs/199-calculation-mode-input/modes.json`, enumerated from the engine by
// `dwsim-runner/scripts/modes.py`. It is NOT a list maintained here: a coverage test whose expected
// set is written by hand agrees only with itself, which is exactly how coverage-gap.md's table came
// to be short by three unit ops and sixteen modes.

using System.Text.Json;
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class CalcModeTests
{
    /// <summary>Every mode-bearing unit op, straight from the engine enum via reflection.</summary>
    private static IEnumerable<(string Wire, CalcModeDef Def)> ModeBearing() =>
        UnitOpCatalog.Types.Values.Where(d => d.CalcMode is not null)
            .OrderBy(d => d.Type, StringComparer.Ordinal)
            .Select(d => (d.Type, d.CalcMode!));

    [Fact]
    public void Every_catalogued_mode_bearing_unit_op_declares_its_mode()
    {
        // 15, measured: the 12 in coverage-gap.md plus heatExchanger (11 modes), splitter and
        // orificePlate. If this number falls, a unit op lost its declaration; if it rises, the
        // engine gained one and modes.json needs re-running.
        Assert.Equal(15, ModeBearing().Count());
    }

    [Fact]
    public void Mode_values_come_from_the_engine_enum_and_are_never_hand_listed()
    {
        foreach (var (wire, def) in ModeBearing())
        {
            var fromEngine = Enum.GetNames(def.EnumType).Select(UnitOpCatalog.NormalizeMode).ToArray();
            var advertised = def.Modes().Select(m => m.Name).ToArray();
            Assert.True(fromEngine.SequenceEqual(advertised),
                $"{wire}: the advertised modes must BE the engine's enum members, in ordinal order. " +
                $"engine=[{string.Join(",", fromEngine)}] advertised=[{string.Join(",", advertised)}]");
        }
    }

    [Fact]
    public void Normalization_round_trips_within_a_unit_op()
    {
        foreach (var (wire, def) in ModeBearing())
            foreach (var member in Enum.GetNames(def.EnumType))
                Assert.True(def.TryResolve(UnitOpCatalog.NormalizeMode(member), out var back) && back == member,
                    $"{wire}: '{member}' must survive normalize -> resolve, got '{back}'");
    }

    [Fact]
    public void The_same_wire_name_may_mean_different_ordinals_on_different_unit_ops()
    {
        // research.md R1 hazard 1, pinned as a test so a future "share the mode vocabulary"
        // refactor fails HERE and not in a converged flowsheet with the wrong answer in it.
        int Ordinal(string wire, string mode)
        {
            var def = UnitOpCatalog.Types[wire].CalcMode!;
            Assert.True(def.TryResolve(mode, out var member), $"{wire} has no mode '{mode}'");
            return (int)Enum.Parse(def.EnumType, member!);
        }

        Assert.Equal(1, Ordinal("pump", "outletPressure"));
        Assert.Equal(0, Ordinal("compressor", "outletPressure"));
        Assert.Equal(0, Ordinal("pump", "deltaP"));
        Assert.Equal(1, Ordinal("compressor", "deltaP"));

        // R1 hazard 2, the other half: two SPELLINGS, one wire name, and it must not collapse
        // the two unit ops into one vocabulary.
        Assert.Equal("Delta_P", Resolve("pump", "deltaP"));
        Assert.Equal("DeltaP", Resolve("valve", "deltaP"));

        static string? Resolve(string wire, string mode)
        {
            UnitOpCatalog.Types[wire].CalcMode!.TryResolve(mode, out var m);
            return m;
        }
    }

    [Fact]
    public void Every_consumed_parameter_is_declared_by_its_own_unit_op()
    {
        // A map naming a parameter the type does not declare is unreachable by construction, and
        // would silently mean "this mode consumes nothing" at the filter.
        foreach (var (wire, def) in ModeBearing())
        {
            var declared = UnitOpCatalog.Types[wire].Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in def.Always.Concat(def.Consumes.Values.SelectMany(v => v)))
                Assert.True(declared.Contains(name),
                    $"{wire}: mode map names '{name}', which is not a parameter of {wire}");
        }
    }

    [Fact]
    public void Every_mode_has_a_consumes_entry_even_when_it_is_empty()
    {
        // An EMPTY consumes list is meaningful and must not be pruned: it is how `energyStream`,
        // `curves` and `head` say "selectable, reads none of your scalar parameters". Absence and
        // emptiness must not be the same thing, or a mode with no entry silently reads nothing.
        foreach (var (wire, def) in ModeBearing())
            foreach (var mode in def.Modes())
                Assert.True(def.Consumes.ContainsKey(mode.Name),
                    $"{wire}: mode '{mode.Name}' has no consumes entry (use an empty array, not absence)");
    }

    [Fact]
    public void The_payload_carries_the_mode_map()
    {
        var payload = JsonSerializer.Serialize(UnitOpCatalog.ToPayload());
        var types = JsonSerializer.Deserialize<JsonElement>(payload);

        var pump = types.EnumerateArray().Single(t => t.GetProperty("type").GetString() == "pump");
        var modes = pump.GetProperty("calcMode").GetProperty("modes").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "deltaP", "outletPressure", "energyStream", "curves", "power" }, modes);

        // A unit op with no mode property carries no `calcMode` at all. Absent means "has no mode",
        // never "not captured yet" — the fail-closed reading spec 055 gives an unset gate.
        var mixer = types.EnumerateArray().Single(t => t.GetProperty("type").GetString() == "mixer");
        Assert.False(mixer.TryGetProperty("calcMode", out _));
    }

    [Fact]
    public void The_advertised_default_is_measured_not_assumed()
    {
        // research R1 said "ordinal 0 is the constructor default in each case" and Vessel disproves
        // it: ordinal 0 is Adiabatic, but three IL sites store ordinal 1 (Legacy) — the default
        // spec 166 measured and worked around. So `default` is a DECLARED measurement, and this
        // asserts the one case where it differs from ordinal 0, which is the case that would
        // otherwise be silently wrong.
        var separator = UnitOpCatalog.Types["separator"].CalcMode!;
        Assert.Equal("adiabatic", separator.Default);
        Assert.NotEqual("legacy", separator.Default);

        foreach (var (wire, def) in ModeBearing())
            Assert.True(def.TryResolve(def.Default, out _), $"{wire}: default '{def.Default}' is not one of its modes");
    }
}
