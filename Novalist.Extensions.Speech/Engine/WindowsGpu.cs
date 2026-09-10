using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.Speech;

internal static class WindowsGpu
{
    // Native Windows support is narrower than Linux ROCm support. Keep this
    // list aligned with the pinned 7.2.1 Windows runtime, not every Radeon GPU.
    internal static string Select(IEnumerable<string> names)
    {
        var adapters = names.Select(name => Regex.Replace(name.ToUpperInvariant()
            .Replace("(TM)", "").Replace("™", ""), @"\s+", " ").Trim()).ToArray();
        if (adapters.Any(name => name.Contains("NVIDIA", StringComparison.Ordinal)))
            return "cuda";
        string[] supported = ["RX 9070 XT", "RX 9070", "RX 9060 XT", "RX 7900 XTX",
            "RX 7700", "AI PRO R9700", "PRO W7900", "PRO W7900 DUAL SLOT"];
        return adapters.Any(name => supported.Any(model => name == "AMD RADEON " + model
            || name == "RADEON " + model)) ? "rocm" : "cpu";
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Enumerates physical display adapters through Windows interop.")]
    internal static string Detect()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "cpu";
        var names = new List<string>();
        for (uint index = 0; ; index++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref adapter, 0))
                break;
            names.Add(adapter.Description);
        }
        return Select(names);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
}
