// dwsim-runner Worker tests — GPL-3.0
//
// ISKS-176 round 2 — the guard on a `lnKeq = f(T)` the engine cannot evaluate.
//
// The failure it closes is SILENT, which is why the valid half of this file matters as much as the
// invalid half. An unevaluable expression is worth ln(Keq) = 0, so Keq = 1, and the reactor then
// CONVERGES on a wrong extent with an empty warnings list. Measured live on `2 H2 + O2 -> 2 H2O` at
// 250 °C (where Gibbs gives ln K ≈ +230): 'TOTAL_GARBAGE', '' and 'lnK' each gave X(H2) = 34.2 % and
// duty = -1.1 kW, byte-identical to a stated constant K = 1.
//
// The refusal is therefore worth having, and the risk it introduces is REFUSING TOO MUCH — a guard
// that rejects a correlation the engine would have solved is a worse bug than the one it replaces,
// because it is unanswerable from the caller's side. So `Accepts` below is the measured grammar, not
// a guess: every string in it was run through a live solve and produced a K other than 1.
using Xunit;

namespace DwsimRunner.Worker.Tests;

public class KeqExpressionTests
{
    /// <summary>
    /// Measured-valid expressions. Each was confirmed live (DWSIM 9.0.5.0, 2026-10-03) to produce a
    /// conversion DIFFERENT from the K=1 fingerprint of 34.2 %, so each is something the engine
    /// really evaluates — not something assumed legal by reading a grammar.
    /// </summary>
    [Theory]
    // The plain correlation form, and the one the WGS integration case uses.
    [InlineData("4577.8/T - 4.33")]
    // DWSIM's OWN built-in Keq expressions, lifted verbatim out of DWSIM.Thermodynamics.dll. If the
    // guard refused these it would refuse the engine's own reaction database.
    [InlineData("-5.4+3465/(T*1.8)")]
    [InlineData("1.587+11160/(T*1.8)")]
    [InlineData("39.5554-177822/(T*1.8)+1.843E+8/(T*1.8)^2-0.8541E+11/(T*1.8)^3+1.4292E+13/(T*1.8)^4")]
    // `^` is exponentiation, not XOR, and E-notation parses.
    [InlineData("T^2")]
    [InlineData("1.843E+8")]
    // Unqualified Math functions — these compile only because the context imports `typeof(Math)`.
    // Drop that import and all three become "unknown identifier" and are falsely refused.
    [InlineData("exp(1)")]
    [InlineData("log(10)")]
    [InlineData("sqrt(4)")]
    // A constant expression is legal; it just means a temperature-independent Keq.
    [InlineData("2*3")]
    [InlineData("15.72")]
    // `T` alone, and the case that matters: an expression that CROSSES ZERO inside the probe range.
    // ln(Keq) = 0 is a perfectly good value — Keq = 1 — at the temperature where it happens. This is
    // exactly why validity is judged by "does it evaluate", never by "is the result nonzero": a
    // zero-at-one-point rule would refuse this correlation, which is real.
    [InlineData("T")]
    [InlineData("4577.8/T - 8.75")]
    public void Accepts(string expression) =>
        Assert.Null(KeqExpression.Refusal(expression));

    /// <summary>The three measured silent-K=1 strings. Each converged, warned nothing, and reported
    /// a wrong extent before this guard existed.</summary>
    [Theory]
    [InlineData("TOTAL_GARBAGE")]
    [InlineData("lnK")]
    // Case matters: `T` is the only variable the engine defines, and `t`, `P`, `R`, `Tc`, `T0` and
    // `x` were each measured producing the 34.2 % K=1 fingerprint. A guard that silently accepted
    // `P` would pass a document straight back into the defect.
    [InlineData("t")]
    [InlineData("P")]
    [InlineData("R")]
    [InlineData("Tc")]
    [InlineData("4577.8/Temperature")]
    public void Refuses_an_expression_the_engine_cannot_evaluate(string expression)
    {
        var why = KeqExpression.Refusal(expression);
        Assert.NotNull(why);
        // The refusal NAMES the expression and says what to write instead. A message that does not
        // is the failure this repo calls F3 — and the thing it replaces is a converged wrong number,
        // so the author has no other signal to work from.
        Assert.Contains(expression, why);
        Assert.Contains("Gibbs Energy", why);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_a_blank_expression_with_its_own_message(string expression)
    {
        var why = KeqExpression.Refusal(expression);
        Assert.NotNull(why);
        Assert.Contains("empty expression", why);
        // Named because it is the easiest of the three to reach by accident: an unset field
        // serialized as "", which is neither a number nor contains "gibbs", so it falls through to
        // the expression branch.
        Assert.Contains("Keq of 1", why);
    }

    /// <summary>A pole inside the operating range is not evaluable everywhere, and the two probe
    /// temperatures are what catch it. One sample could sit on either side and pass.</summary>
    [Fact]
    public void Refuses_an_expression_that_is_not_finite()
    {
        // 1/(T-523.15) is infinite at exactly the second probe point (250 °C).
        var why = KeqExpression.Refusal("1/(T-523.15)");
        Assert.NotNull(why);
        Assert.Contains("finite", why);
    }
}
