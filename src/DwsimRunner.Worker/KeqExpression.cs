// dwsim-runner Worker — GPL-3.0
//
// ISKS-176 round 2 — an equilibrium reaction's `lnKeq = f(T)` that the engine CANNOT EVALUATE is
// silently worth zero, and `exp(0)` is 1.
//
// WHAT WAS MEASURED (live, DWSIM 9.0.5.0, 2026-10-03). `2 H2 + O2 -> 2 H2O` at 250 °C, where Gibbs
// gives ln K ≈ +230, run with `equilibriumConstantSource` set to three unevaluable strings:
//
//     'TOTAL_GARBAGE'  ->  X(H2) = 34.2 %, xH2O = 0.250, duty = -1.1 kW, warnings = []
//     ''               ->  X(H2) = 34.2 %, xH2O = 0.250, duty = -1.1 kW, warnings = []
//     'lnK'            ->  X(H2) = 34.2 %, xH2O = 0.250, duty = -1.1 kW, warnings = []
//
// All three are BYTE-IDENTICAL to a stated constant of K = 1, and all three CONVERGE with an empty
// warnings list. That is the whole defect: not a crash, not a diverged run, but a plausible number
// — a partial conversion with a matching partial duty, mass balance closing — carrying the engine's
// provenance. A reporter spent a bisection on it and concluded the extent solver was clamping,
// because an extent of 14.4 mol/h is exactly what a real equilibrium at K ≈ 0.46 looks like. The
// extent solver is correct; it was handed the wrong K.
//
// WHY THE CHECK IS FLEE AND NOT A REGEX
//
// `T^2`, `exp(1)`, `log(10)`, `sqrt(4)`, `1.843E+8` and DWSIM's own built-in
// `-5.4+3465/(T*1.8)` are all VALID and all measured working. Any hand-written grammar that admits
// those and rejects `TOTAL_GARBAGE` is a second opinion about what DWSIM accepts, and the first
// time it disagrees it refuses a document the engine would have solved. So the check compiles the
// candidate through `Ciloci.Flee` — the evaluator DWSIM itself uses, loaded from the same
// DWSIM_PATH — and refuses only what that parser rejects. There is no second grammar to drift.
//
// `T` IS THE ONLY VARIABLE, AND IT IS CASE-SENSITIVE. Measured, because the context below has to
// define exactly what the engine's does or it would refuse valid expressions: `T` resolves, and
// `t`, `P`, `R`, `Tc`, `T0` and `x` every one of them produced the 34.2 % K=1 fingerprint. A single
// `double` variable named `T` is the entire symbol table.
using Ciloci.Flee;

namespace DwsimRunner.Worker;

internal static class KeqExpression
{
    /// <summary>
    /// Two temperatures, not one. An expression is evaluated at both and must be finite at both.
    ///
    /// One sample cannot distinguish "unevaluable" from "evaluates to zero here": `4577.8/T - 4.33`
    /// is a perfectly good correlation that happens to cross zero near 1057 K, and refusing it
    /// because the probe landed there would be the false refusal this guard exists to avoid. Two
    /// well-separated points also catch a pole (`1/(T-500)`), which is infinite at one and finite at
    /// the other.
    ///
    /// These are probe points for VALIDITY only — nothing here decides the K the solve uses. The
    /// engine evaluates the same expression itself, at the reactor's own temperature.
    /// </summary>
    private static readonly double[] ProbeKelvin = [298.15, 523.15];

    /// <summary>
    /// `T` replaced by its value — SUBSTITUTED, not bound as a variable, because that is what the
    /// engine measurably does.
    ///
    /// Binding `context.Variables["T"]` was the obvious first implementation and it is wrong in a way
    /// only measurement catches: FLEE resolves variables case-INSENSITIVELY, so a bound variable
    /// accepts `t` — while the engine was measured treating `t` as undefined (the 34.2 % K=1
    /// fingerprint). Forcing `Options.CaseSensitive = true` fixes `t` and then breaks `exp(1)`,
    /// `log(10)` and `sqrt(4)`, which are measured VALID, because `Math`'s members are `Exp`/`Log`/
    /// `Sqrt`. No single FLEE setting reproduces both halves.
    ///
    /// Substitution does: the engine is case-sensitive about `T` and case-insensitive about Math
    /// members precisely because `T` never reaches the parser as an identifier at all. This matches
    /// all 20 live measurements — `T` and `T*1.8` resolve; `t`, `P`, `R`, `Tc`, `T0` and
    /// `Temperature` do not.
    ///
    /// Word boundaries are what keep `T0` and `Tc` undefined (a bare `Replace("T", …)` would turn
    /// `T0` into `523.150`, a VALID number, and silently accept an expression the engine refuses).
    /// Parenthesised so a negative value cannot form `--` or bind looser than intended.
    /// </summary>
    private static string Substitute(string expression, double kelvin) =>
        System.Text.RegularExpressions.Regex.Replace(
            expression, @"\bT\b", $"({kelvin.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");

    /// <summary>
    /// Why this `lnKeq = f(T)` cannot be used, or null when it can.
    ///
    /// Returns a message naming the expression and what to write instead, because the alternative
    /// the caller actually experienced was a converged flowsheet with a wrong extent.
    /// </summary>
    internal static string? Refusal(string expression)
    {
        // Blank is its own message. It reaches `KOpt.Expression` only because "" is neither a
        // number nor contains "gibbs", and it is the easiest of the three to write by accident —
        // an unset field serialized as an empty string.
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "the equilibrium constant is an empty expression, which the engine evaluates as " +
                   "ln(Keq) = 0 — a silent Keq of 1. State a correlation in T (kelvin), e.g. " +
                   "'4577.8/T - 4.33', a bare number for a constant Keq, or 'Gibbs Energy' to let " +
                   "the engine compute it from formation energies.";
        }

        foreach (var t in ProbeKelvin)
        {
            double value;
            try
            {
                var context = new ExpressionContext();
                // `Math` imported unqualified, which is what makes the measured `exp(1)`, `log(10)`
                // and `sqrt(4)` compile. Without this import those three would be "unknown
                // identifier" here and valid to the engine — a false refusal.
                context.Imports.AddType(typeof(Math));
                value = context.CompileGeneric<double>(Substitute(expression, t)).Evaluate();
            }
            catch (Exception ex)
            {
                // The parser's own words, kept. "Unknown identifier TOTAL_GARBAGE" tells the author
                // which token is wrong; collapsing it to "invalid expression" is the refusal that
                // names nothing.
                return $"the equilibrium constant expression '{expression}' is not something the " +
                       $"engine can evaluate ({ex.GetBaseException().Message}). It is read as " +
                       "ln(Keq) as a function of T in KELVIN, and an expression that does not " +
                       "evaluate is taken as ln(Keq) = 0 — a silent Keq of 1, which converges and " +
                       "reports a wrong extent. Use a bare number for a constant Keq, or " +
                       "'Gibbs Energy'.";
            }

            if (!double.IsFinite(value))
            {
                return $"the equilibrium constant expression '{expression}' evaluates to " +
                       $"{value} at T = {t:G6} K. ln(Keq) must be a finite number at every " +
                       "temperature the reactor may reach.";
            }
        }
        return null;
    }
}
