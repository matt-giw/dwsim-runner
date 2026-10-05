// dwsim-runner Worker tests — GPL-3.0
// Tests that run the REAL engine need its native libraries to load on this CPU. The DWSIM bundle
// ships x86-64 libraries, so on an Apple-silicon container those tests SKIP with this reason
// rather than fail; on x86-64 (CI) they run.

using System.Runtime.InteropServices;

namespace DwsimRunner.Worker.Tests;

internal static class EngineNatives
{
    /// <summary>Null when libSkiaSharp in DWSIM_PATH is built for this CPU; otherwise why not.</summary>
    internal static string? MismatchReason()
    {
        var lib = Path.Combine(DwsimResolver.DwsimPath, "libSkiaSharp.so");
        if (!File.Exists(lib)) return $"no {lib}";
        var header = new byte[20];
        using (var f = File.OpenRead(lib)) f.ReadExactly(header);
        var machine = BitConverter.ToUInt16(header, 18);   // ELF e_machine
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (ushort)62,     // EM_X86_64
            Architecture.Arm64 => (ushort)183,  // EM_AARCH64
            _ => (ushort)0,
        };
        return machine == expected ? null
            : $"the engine's native libraries ({lib}, ELF machine {machine}) are not built for {RuntimeInformation.ProcessArchitecture}";
    }
}
