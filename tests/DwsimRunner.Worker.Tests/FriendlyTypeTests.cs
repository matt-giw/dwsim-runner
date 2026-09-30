// dwsim-runner Worker tests — GPL-3.0
// ISK-442 — a unit op's reported `type` is DERIVED from UnitOpCatalog.Types. Two hand-written copies
// of the mapping reported all five reactors as "reactor", a type no document can carry, so a result
// row could not be joined back to the type the caller asked for.

using DWSIM.Interfaces.Enums.GraphicObjects;
using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

// Engine types travel as STRINGS in [InlineData]: xUnit reads attribute arguments during
// discovery, before TestSetup's module initializer installs the DWSIM resolver, so an
// `ObjectType` argument fails discovery — and a discovery failure is not counted as a failed test.
public class FriendlyTypeTests
{
    [Theory]
    [InlineData("RCT_Conversion", "Reactor_Conversion", "reactorConversion")]
    [InlineData("RCT_Equilibrium", "Reactor_Equilibrium", "reactorEquilibrium")]
    [InlineData("RCT_Gibbs", "Reactor_Gibbs", "reactorGibbs")]
    [InlineData("RCT_CSTR", "Reactor_CSTR", "reactorCSTR")]
    [InlineData("RCT_PFR", "Reactor_PFR", "reactorPFR")]
    public void Each_reactor_reports_its_own_catalog_type(string engineType, string clrName, string expected)
    {
        Assert.Equal(expected, Engine.FriendlyType(Enum.Parse<ObjectType>(engineType), clrName));
    }

    [Fact]
    public void Every_catalog_type_round_trips_through_its_engine_type()
    {
        // The document names a unit op by wire type; the solve must report the SAME name back.
        foreach (var def in UnitOpCatalog.Types.Values)
            Assert.Equal(def.Type, Engine.FriendlyType(def.ObjectType, "Irrelevant"));
    }

    [Theory]
    // The mappings the hand-written copies carried, now reached through the catalog.
    [InlineData("Vessel", "Vessel", "separator")]
    [InlineData("OT_Recycle", "Recycle", "recycle")]
    [InlineData("Expander", "Expander", "expander")]
    [InlineData("HeatExchanger", "HeatExchanger", "heatExchanger")]
    [InlineData("WaterElectrolyzer", "WaterElectrolyzer", "waterElectrolyzer")]
    public void Previously_hand_mapped_types_are_unchanged(string engineType, string clrName, string expected)
    {
        Assert.Equal(expected, Engine.FriendlyType(Enum.Parse<ObjectType>(engineType), clrName));
    }

    [Theory]
    // No graphic object: a CLR class name that IS an ObjectType member still resolves via the catalog.
    [InlineData("Vessel", "separator")]
    [InlineData("Pump", "pump")]
    [InlineData("WaterElectrolyzer", "waterElectrolyzer")]
    // A type the catalog does not expose keeps the old fallback: the camel-cased class name.
    [InlineData("Filter", "filter")]
    [InlineData("AbsorptionColumn", "absorptionColumn")]
    public void Without_an_engine_type_the_class_name_decides(string clrName, string expected)
    {
        Assert.Equal(expected, Engine.FriendlyType(null, clrName));
    }
}
