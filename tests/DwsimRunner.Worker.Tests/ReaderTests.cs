// dwsim-runner Worker tests — GPL-3.0
// iskra spec 285 (ISK-485) — the `read` mode's getters, against the REAL DWSIM classes.
//
// GP-19 (probe before bind) in the read direction: the reader emits a catalog parameter only if its
// getter is exercised here. The theory below is generated from the catalog itself, so a parameter
// added to the catalog is round-tripped (ApplyParameter → ReadParameters) without anyone
// remembering to add a case — and fails here if the read-back is not faithful.

using System.Text.Json;
using System.Text.Json.Nodes;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums.GraphicObjects;
using DWSIM.UnitOperations.UnitOperations;
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class ReaderTests
{
    private static readonly Dictionary<string, Func<ISimulationObject>> Construct = new()
    {
        ["mixer"] = () => new Mixer(),
        ["splitter"] = () => new Splitter(),
        ["separator"] = () => new Vessel(),
        ["tank"] = () => new Tank(),
        ["heater"] = () => new Heater(),
        ["cooler"] = () => new Cooler(),
        ["heatExchanger"] = () => new HeatExchanger(),
        ["componentSeparator"] = () => new ComponentSeparator(),
        ["pump"] = () => new Pump(),
        ["compressor"] = () => new Compressor(),
        ["expander"] = () => new Expander(),
        ["valve"] = () => new Valve(),
        ["pipe"] = () => new Pipe(),
        ["orificePlate"] = () => new OrificePlate(),
        ["reactorConversion"] = () => new DWSIM.UnitOperations.Reactors.Reactor_Conversion(),
        ["reactorEquilibrium"] = () => new DWSIM.UnitOperations.Reactors.Reactor_Equilibrium(),
        ["reactorGibbs"] = () => new DWSIM.UnitOperations.Reactors.Reactor_Gibbs(),
        ["reactorCSTR"] = () => new DWSIM.UnitOperations.Reactors.Reactor_CSTR(),
        ["reactorPFR"] = () => new DWSIM.UnitOperations.Reactors.Reactor_PFR(),
        ["shortcutColumn"] = () => new ShortcutColumn(),
        ["waterElectrolyzer"] = () => new WaterElectrolyzer(),
        ["recycle"] = () => new DWSIM.UnitOperations.SpecialOps.Recycle(),
    };

    // A distinct value per unit type, deliberately NOT the engine default, so an emitted default
    // cannot pass for a read-back.
    private static string Value(ParamDef p) => p.UnitType switch
    {
        "temperature" => """{"value":345.6,"unit":"K"}""",
        "temperatureDelta" => """{"value":12.5,"unit":"K"}""",
        "pressure" => """{"value":123456.0,"unit":"Pa"}""",
        "power" => """{"value":42.5,"unit":"kW"}""",
        "massFlow" => """{"value":1.25,"unit":"kg/s"}""",
        "molarFlow" => """{"value":3.5,"unit":"mol/s"}""",
        "length" => """{"value":3.5,"unit":"m"}""",
        "area" => """{"value":2.5,"unit":"m2"}""",
        "volume" => """{"value":1.5,"unit":"m3"}""",
        "heatTransferCoefficient" => """{"value":250.0,"unit":"W/[m2.K]"}""",
        "voltage" => """{"value":500.0,"unit":"V"}""",
        "integer" => "7",
        "string" => "\"Methane\"",
        _ => "0.37",
    };

    /// <summary>Every catalog parameter the reader may emit for a type the reader reads generically.</summary>
    public static IEnumerable<object[]> ReadableParameters() =>
        UnitOpCatalog.Types.Values
            .Where(d => d.Type != "distillationColumn")   // ColumnConfigurator's own surface; see the column test
            .SelectMany(d => d.Parameters
                .Where(p => p.Name != "calcMode" && p.EngineProperties.Length > 0
                            && !Reader.NotReadBack.Contains((d.Type, p.Name)))
                .Select(p => new object[] { d.Type, p.Name }));

    [Theory]
    [MemberData(nameof(ReadableParameters))]
    public void Every_read_back_parameter_round_trips_through_the_builders_own_setter(string type, string param)
    {
        var def = UnitOpCatalog.Types[type];
        var p = def.Parameters.Single(x => x.Name == param);
        var so = Construct[type]();

        // Put the unit in a mode that READS this parameter, as ResolveCalcMode would.
        if (def.CalcMode is { } cm)
        {
            var mode = cm.Modes().First(m => cm.Reads(m.Name, param));
            var prop = so.GetType().GetProperty(cm.ClrProperty)!;
            prop.SetValue(so, Enum.Parse(prop.PropertyType, mode.EngineMember));
        }

        // A splitter's ratio list is sized by its connected outlets; a built one has two.
        if (so is Splitter sp && sp.Ratios.Count == 0) { sp.Ratios.Add(0.5); sp.Ratios.Add(0.5); }

        var raw = JsonSerializer.Deserialize<JsonElement>(Value(p));
        var unit = new FlowObject("U-1", "unitOp", type, null,
            new Dictionary<string, JsonElement> { [param] = raw }, null);
        var issues = new List<string>();
        FlowsheetBuilder.ApplyParameter(so, unit, def, p, raw, ["Methane", "Ethane"],
            (code, _, message, _) => issues.Add($"{code}: {message}"));
        Assert.Empty(issues);

        var bag = Reader.ReadParameters(so, def, out _);

        Assert.True(bag.ContainsKey(param), $"{type}.{param} was not read back; got {bag.ToJsonString()}");
        var expected = JsonNode.Parse(Value(p))!;
        var actual = bag[param]!;
        if (expected is JsonObject eq)
        {
            Assert.Equal((string?)eq["unit"], (string?)actual["unit"]);
            Assert.Equal((double)eq["value"]!, (double)actual["value"]!, 6);
        }
        else if (p.UnitType == "string")
            Assert.Equal((string)expected!, (string)actual!);
        else if (p.UnitType == "integer")
            Assert.Equal((int)expected!, (int)actual!);
        else
            Assert.Equal((double)expected!, (double)actual!, 6);
    }

    [Fact]
    public void Parameters_that_have_no_faithful_getter_are_never_emitted()
    {
        foreach (var (type, param) in Reader.NotReadBack)
        {
            var bag = Reader.ReadParameters(Construct[type](), UnitOpCatalog.Types[type], out _);
            Assert.False(bag.ContainsKey(param), $"{type}.{param} is declared unreadable and was emitted");
        }
    }

    // GP-3 — a value the active mode does not READ is a result, not a specification. A heater solved
    // to an outlet temperature holds the duty it computed; that duty must not come back as a spec.
    [Fact]
    public void A_value_the_calculation_mode_does_not_read_is_not_emitted()
    {
        var heater = new Heater
        {
            CalcMode = Heater.CalculationMode.OutletTemperature,
            OutletTemperature = 360,
            DeltaQ = 123.4,   // what a solve would leave behind
        };

        var bag = Reader.ReadParameters(heater, UnitOpCatalog.Types["heater"], out var mode);

        Assert.Equal("outletTemperature", mode);
        Assert.Equal("outletTemperature", (string?)bag["calcMode"]);
        Assert.Equal(360, (double)bag["outletTemperature"]!["value"]!, 6);
        Assert.False(bag.ContainsKey("heatDuty"), "the computed duty was emitted as a specification");

        // ...and the same duty IS emitted when the mode reads it — the filter is the mode, not the name.
        heater.CalcMode = Heater.CalculationMode.HeatAdded;
        Assert.Equal(123.4, (double)Reader.ReadParameters(heater, UnitOpCatalog.Types["heater"], out _)["heatDuty"]!["value"]!, 6);
    }

    // #31 review finding 3 — a GATED parameter is a specification only when its gates are set on the
    // engine object. A Kv valve whose file never enabled the opening↔Kv relationship holds the default
    // opening (50 %); emitting it, the build then enables the relationship and the Kv changes.
    [Fact]
    public void A_gated_parameter_whose_gate_is_off_is_not_emitted()
    {
        var def = UnitOpCatalog.Types["valve"];
        var valve = new Valve { CalcMode = Valve.CalculationMode.Kv_General, Kv = 20 };
        valve.GetType().GetProperty("EnableOpeningKvRelationship")!.SetValue(valve, false);

        var bag = Reader.ReadParameters(valve, def, out var mode);

        Assert.Equal("kvGeneral", mode);
        Assert.True(bag.ContainsKey("kv"));
        Assert.False(bag.ContainsKey("openingPct"), $"a default opening was emitted as a spec: {bag.ToJsonString()}");
    }

    [Fact]
    public void A_collapsed_mode_reads_back_as_its_survivor()
    {
        var heater = new Heater { CalcMode = Heater.CalculationMode.HeatAddedRemoved };
        Reader.ReadParameters(heater, UnitOpCatalog.Types["heater"], out var mode);
        Assert.Equal("heatAdded", mode);
    }

    [Fact]
    public void The_rigorous_column_reads_back_what_ColumnConfigurator_wrote()
    {
        // The bare constructor builds no stages and the one AddObject uses needs a flowsheet
        // (AddStages dereferences it), so the stages are made here — numberOfStages itself is read
        // off `Stages.Count`, which is what SetNumberOfStages maintains.
        var col = new DistillationColumn();
        for (var i = 0; i < 14; i++)
            col.Stages.Add(new DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps.Stage(Guid.NewGuid().ToString()));
        var doc = new FlowObject("DC-1", "unitOp", "distillationColumn", null, null, null);
        void Apply(string name, string json) => ColumnConfigurator.Apply(col, name, JsonSerializer.Deserialize<JsonElement>(json));
        Apply("refluxRatio", "2.5");
        Apply("bottomsMolarFlow", """{"value":75.0,"unit":"mol/s"}""");
        Apply("condenserPressure", """{"value":101325.0,"unit":"Pa"}""");
        Apply("reboilerPressure", """{"value":121325.0,"unit":"Pa"}""");
        Apply("solvingMethod", "\"naphtaliSandholm\"");
        Apply("maxIterations", "250");
        ColumnConfigurator.Finish(col, doc with { Parameters = new() { ["maxIterations"] = default } });

        // ConnectFeed records the stage by NAME (first round trip lost feedStage reading it as an ID).
        for (var i = 0; i < col.Stages.Count; i++) col.Stages[i].Name = $"Stage_{i + 1}";
        col.MaterialStreams["feed-info"] = new DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps.StreamInformation
        {
            StreamID = "feed-object", AssociatedStage = "Stage_10",
            StreamBehavior = DWSIM.UnitOperations.UnitOperations.Auxiliary.SepOps.StreamInformation.Behavior.Feed,
        };

        var (bag, wired, detail) = Reader.ReadColumn(col, new() { ["feed-object"] = "FEED" });

        Assert.Null(detail);
        Assert.Contains(("FEED", "Feed"), wired);
        Assert.Equal(10, (int)bag["feedStage"]!);
        Assert.Equal(14, (int)bag["numberOfStages"]!);
        Assert.Equal(2.5, (double)bag["refluxRatio"]!, 6);
        Assert.Equal(75.0, (double)bag["bottomsMolarFlow"]!["value"]!, 6);
        Assert.Equal("mol/s", (string?)bag["bottomsMolarFlow"]!["unit"]);
        Assert.Equal(101325.0, (double)bag["condenserPressure"]!["value"]!, 3);
        Assert.Equal(121325.0, (double)bag["reboilerPressure"]!["value"]!, 3);
        Assert.Equal("naphtaliSandholm", (string?)bag["solvingMethod"]);
        Assert.Equal(250, (int)bag["maxIterations"]!);
    }

    [Theory]
    [InlineData(ObjectType.CapeOpenUO, "CAPE_OPEN")]
    [InlineData(ObjectType.CustomUO, "SCRIPT_BLOCK")]
    [InlineData(ObjectType.ExcelUO, "SCRIPT_BLOCK")]
    [InlineData(ObjectType.Controller_Python, "SCRIPT_BLOCK")]
    [InlineData(ObjectType.OT_Adjust, "LOGICAL_BLOCK")]
    [InlineData(ObjectType.OT_Spec, "LOGICAL_BLOCK")]
    [InlineData(ObjectType.OT_EnergyRecycle, "LOGICAL_BLOCK")]
    [InlineData(ObjectType.Controller_PID, "LOGICAL_BLOCK")]
    [InlineData(ObjectType.AbsorptionColumn, "NOT_IN_CATALOG")]
    [InlineData(ObjectType.FlowsheetUO, "NOT_IN_CATALOG")]
    public void Placeholder_reasons(ObjectType t, string reason) =>
        Assert.Equal(reason, Reader.PlaceholderReason(t));

    [Fact]
    public void The_load_failure_chain_names_every_level_innermost_last()
    {
        var ex = new TypeInitializationException("DWSIM.Logging.Logger",
            new InvalidOperationException("outer cause",
                new System.IO.FileNotFoundException("Could not load file or assembly 'Real.Cause'.")));

        var lines = Reader.Chain(ex).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("System.TypeInitializationException: ", lines[0]);
        Assert.Contains("DWSIM.Logging.Logger", lines[0]);
        Assert.Contains("System.InvalidOperationException: outer cause", lines[1]);
        Assert.Contains("System.IO.FileNotFoundException: Could not load file or assembly 'Real.Cause'.", lines[2]);
    }

    // #31 review finding 4 — the message names the cause, not the server's file layout.
    [Fact]
    public void The_chain_redacts_absolute_paths_and_keeps_the_cause()
    {
        var ex = new InvalidOperationException("Error Loading Property Package Information",
            new UnauthorizedAccessException("Access to the path '/opt/dwsim/DWSIM Application Data' is denied."));

        var text = Reader.Chain(ex);

        Assert.DoesNotContain("/opt/dwsim", text);
        Assert.Contains("Access to the path '<path>' is denied.", text);
        Assert.Contains("Error Loading Property Package Information", text);
    }

    [Fact]
    public void The_chain_is_capped()
    {
        Exception ex = new("innermost");
        for (var i = 0; i < 200; i++) ex = new InvalidOperationException(new string('x', 50), ex);

        Assert.True(Reader.Chain(ex).Length <= 2048);
    }
}
