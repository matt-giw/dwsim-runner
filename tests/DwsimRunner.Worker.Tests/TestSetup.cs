// dwsim-runner Worker tests — GPL-3.0
// DWSIM assemblies resolve from DWSIM_PATH exactly as they do in the Worker
// process; the module initializer runs before any DWSIM type is JIT'd.

using System.Runtime.CompilerServices;
using DwsimRunner.Worker;

namespace DwsimRunner.Worker.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    public static void Init() => DwsimResolver.Install();
}
