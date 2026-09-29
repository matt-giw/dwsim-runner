// dwsim-runner Worker tests — GPL-3.0
// DWSIM's converter has no voltage family: `ConvertToSI("kV", x)` returns x. The worker scales
// voltage itself, and an unknown unit is refused rather than passed through.

using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class VoltageConversionTests
{
    [Theory]
    [InlineData("V", 1.9, 1.9)]
    [InlineData("kV", 1.2, 1200.0)]
    [InlineData("mV", 500.0, 0.5)]
    public void Voltage_scales_to_volts(string unit, double given, double expectedV)
    {
        Assert.Equal(expectedV, UnitOpCatalog.ConvertVoltage(unit, given), 9);
    }

    [Theory]
    [InlineData("MV")]
    [InlineData("volt")]
    [InlineData("bar")]
    public void An_unknown_voltage_unit_is_refused(string unit)
    {
        Assert.Throws<InvalidOperationException>(() => UnitOpCatalog.ConvertVoltage(unit, 1.0));
    }

}
