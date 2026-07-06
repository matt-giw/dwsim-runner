// dwsim-runner Worker — GPL-3.0
// Resolves DWSIM (and its dependency) assemblies from DWSIM_PATH at runtime,
// so we never redistribute them and one build serves SaaS + on-prem.

using System.Reflection;
using System.Runtime.Loader;

namespace DwsimRunner.Worker;

internal static class DwsimResolver
{
    public static string DwsimPath { get; private set; } = "";

    /// <summary>Must be called before ANY type that touches DWSIM is JIT'd.</summary>
    public static void Install()
    {
        DwsimPath = Environment.GetEnvironmentVariable("DWSIM_PATH") ?? "/opt/dwsim";

        if (!File.Exists(Path.Combine(DwsimPath, "DWSIM.Automation.dll")))
            throw new InvalidOperationException(
                $"DWSIM not found at '{DwsimPath}'. Install DWSIM (https://dwsim.org) " +
                "and set DWSIM_PATH to its install directory. " +
                "macOS example: /Applications/DWSIM.app/Contents/MonoBundle");

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(DwsimPath, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };

        // Native deps (libSkiaSharp, CoolProp, etc.) live alongside the managed DLLs.
        AppendToEnv("LD_LIBRARY_PATH", DwsimPath);    // linux
        AppendToEnv("DYLD_LIBRARY_PATH", DwsimPath);  // macOS
    }

    private static void AppendToEnv(string key, string value)
    {
        var existing = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key,
            string.IsNullOrEmpty(existing) ? value : $"{value}:{existing}");
    }
}
