// dwsim-runner Worker tests — GPL-3.0
// iskra 316 — the two unit kinds the generators introduce. Measured first (T002): whether DWSIM's
// converter has a velocity family at all, and the fact that the engine stores irradiance in kW/m2,
// which is NOT the SI the converter targets. Each kind follows the voltage precedent: an explicit
// table in the worker, an unknown spelling refused rather than passed through unchanged.

using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class GeneratorUnitTests
{
    [Fact]
    public void Engine_converter_velocity_family_is_measured()
    {
        // 36 km/h is 10 m/s. If the converter has no velocity family it returns 36 unchanged —
        // the 213 trap — and the worker must carry its own table.
        var si = DWSIM.SharedClasses.SystemsOfUnits.Converter.ConvertToSI("km/h", 36.0);
        Assert.True(si == 36.0 || Math.Abs(si - 10.0) < 1e-9, $"unexpected converter answer {si}");
    }

    [Theory]
    [InlineData("m/s", 10.0, 10.0)]
    [InlineData("km/h", 36.0, 10.0)]
    [InlineData("ft/s", 1.0, 0.3048)]
    public void Velocity_scales_to_metres_per_second(string unit, double given, double expected)
    {
        Assert.Equal(expected, UnitOpCatalog.ConvertVelocity(unit, given), 9);
    }

    [Theory]
    [InlineData("kW/m2", 1.0, 1.0)]
    [InlineData("W/m2", 850.0, 0.85)]
    public void Irradiance_scales_to_kilowatts_per_square_metre(string unit, double given, double expected)
    {
        // The engine's SolarIrradiation_kW_m2 is in kW/m2; SI would be a thousand times off.
        Assert.Equal(expected, UnitOpCatalog.ConvertIrradiance(unit, given), 9);
    }

    [Theory]
    [InlineData("mph")]
    [InlineData("knots")]
    public void An_unknown_velocity_unit_is_refused(string unit)
    {
        Assert.Throws<InvalidOperationException>(() => UnitOpCatalog.ConvertVelocity(unit, 1.0));
    }

    [Theory]
    [InlineData("W/cm2")]
    [InlineData("sun")]
    public void An_unknown_irradiance_unit_is_refused(string unit)
    {
        Assert.Throws<InvalidOperationException>(() => UnitOpCatalog.ConvertIrradiance(unit, 1.0));
    }

    [Fact]
    public void The_three_exposed_units_and_only_those_are_in_the_catalog()
    {
        foreach (var t in new[] { "windTurbine", "solarPanel", "solidsSeparator" })
            Assert.True(UnitOpCatalog.Types.ContainsKey(t), $"{t} missing");
        // The probe entries for units that did not solve (research R1) must not ship: a catalog entry
        // that cannot solve is the stub 099 forbids.
        foreach (var t in new[] { "hydroelectricTurbine", "pemFuelCell", "filter", "absorptionColumn", "reactorGibbsReaktoro" })
            Assert.False(UnitOpCatalog.Types.ContainsKey(t), $"{t} must not be exposed");
    }
}
