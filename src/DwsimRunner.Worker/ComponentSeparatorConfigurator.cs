// dwsim-runner Worker — GPL-3.0
// Spec 099 US2 — the component separator's per-compound specification.
//
// Every other unit op is configured by name→property reflection, one friendly name to one scalar.
// This type's specification is a `Dictionary<string, ComponentSeparationSpec>` keyed by compound, so
// reflection cannot express it and it gets a bespoke handler — the same shape as
// `ColumnConfigurator`, including the "not my type → return false" guard so the generic path still
// serves everything else.
//
// A COMPOUND IN THE SPECIFICATION THAT IS NOT IN THE FLOWSHEET IS A NAMED BUILD ISSUE, never a
// silent skip. A silently dropped separation spec is a converged flowsheet with the wrong split,
// which is the exact failure class this whole feature exists to close.

using System.Text.Json;
using DWSIM.Interfaces;
using DWSIM.UnitOperations.UnitOperations;
using DWSIM.UnitOperations.UnitOperations.Auxiliary;

namespace DwsimRunner.Worker;

internal static class ComponentSeparatorConfigurator
{
    /// <summary>The parameters this class owns; the generic setter must not see them.</summary>
    public static bool Handles(string paramName) =>
        string.Equals(paramName, "separationSpecs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apply a per-compound separation specification.
    /// </summary>
    /// <remarks>
    /// Wire shape, one entry per compound:
    ///   { "Water": { "spec": "PercentInletMassFlow", "value": 98.5 }, ... }
    ///
    /// `spec` names a `SeparationSpec` member — MassFlow, MolarFlow, PercentInletMassFlow or
    /// PercentInletMolarFlow — and defaults to `PercentInletMassFlow`, which is what a recovery
    /// fraction means and the only mode the app currently projects.
    /// </remarks>
    public static void Apply(ISimulationObject so, JsonElement raw, FlowObject unitDoc,
        IReadOnlyCollection<string> flowsheetCompounds,
        Action<string, string?, string, string?> error)   // (code, tag, message, path)
    {
        if (so is not ComponentSeparator sep) return;
        if (raw.ValueKind != JsonValueKind.Object)
        {
            error("INVALID_PARAMETER_VALUE", unitDoc.Tag,
                "separationSpecs must be an object keyed by compound name, e.g. " +
                "{\"Water\": {\"spec\": \"PercentInletMassFlow\", \"value\": 98.5}}",
                "objects[].parameters.separationSpecs");
            return;
        }

        foreach (var entry in raw.EnumerateObject())
        {
            // Resolved case-insensitively against the flowsheet's OWN compound list, because that is
            // what the engine keys the dictionary by. A near-miss here is the silent-wrong-split bug.
            var compound = flowsheetCompounds.FirstOrDefault(c =>
                string.Equals(c, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (compound is null)
            {
                error("UNKNOWN_COMPOUND", unitDoc.Tag,
                    $"separationSpecs names '{entry.Name}', which is not one of this flowsheet's " +
                    $"compounds ({string.Join(", ", flowsheetCompounds)}). The split it describes " +
                    "would be silently ignored, so the document is refused instead.",
                    "objects[].parameters.separationSpecs");
                continue;
            }

            double value;
            var mode = "PercentInletMassFlow";
            if (entry.Value.ValueKind == JsonValueKind.Number)
            {
                value = entry.Value.GetDouble();
            }
            else if (entry.Value.ValueKind == JsonValueKind.Object)
            {
                if (!entry.Value.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number)
                {
                    error("INVALID_PARAMETER_VALUE", unitDoc.Tag,
                        $"separationSpecs['{entry.Name}'] has no numeric 'value'",
                        "objects[].parameters.separationSpecs");
                    continue;
                }
                value = v.GetDouble();
                if (entry.Value.TryGetProperty("spec", out var m) && m.GetString() is { Length: > 0 } named)
                    mode = named;
            }
            else
            {
                error("INVALID_PARAMETER_VALUE", unitDoc.Tag,
                    $"separationSpecs['{entry.Name}'] must be a number or {{spec, value}}",
                    "objects[].parameters.separationSpecs");
                continue;
            }

            if (!Enum.TryParse<SeparationSpec>(mode, ignoreCase: true, out var sepSpec))
            {
                error("INVALID_PARAMETER_VALUE", unitDoc.Tag,
                    $"separationSpecs['{entry.Name}'].spec is '{mode}'; accepted: " +
                    string.Join(", ", Enum.GetNames<SeparationSpec>()),
                    "objects[].parameters.separationSpecs");
                continue;
            }

            sep.ComponentSepSpecs[compound] = new ComponentSeparationSpec
            {
                ComponentID = compound,
                SepSpec = sepSpec,
                SpecValue = value,
                SpecUnit = "",
            };
        }
    }
}
