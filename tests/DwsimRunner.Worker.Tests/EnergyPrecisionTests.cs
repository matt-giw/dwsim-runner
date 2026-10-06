using DwsimRunner.Worker;
using Xunit;

namespace DwsimRunner.Worker.Tests;

// iskra spec 285 (Matt, 2026-10-05) — an energy row carries the engine's duty at full precision.
// It was rounded to 0.1 kW, which made any duty under 50 kW fail a 0.1 % comparison by rounding
// alone: opensteam's pump, 0.546 kW in the file, came back 0.5.
public class EnergyPrecisionTests
{
    [Fact]
    public void An_energy_row_carries_the_engines_duty_unrounded()
    {
        Assert.Equal(0.54591963786995, Modes.EnergyFlowKw(0.54591963786995)!.Value, 12);
        Assert.Equal(101.31988784426, Modes.EnergyFlowKw(101.31988784426)!.Value, 12);
        Assert.Null(Modes.EnergyFlowKw(double.NaN));
        Assert.Null(Modes.EnergyFlowKw(null));
    }
}
