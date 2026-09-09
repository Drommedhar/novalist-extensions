using System.Runtime.InteropServices;

namespace Novalist.Extensions.Speech;

internal static class SpeechRuntime
{
    public static bool UseMlx => UsesMlx(OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);

    internal static bool UsesMlx(bool macOS, Architecture architecture)
        => macOS && architecture == Architecture.Arm64;

    public static string RequirementsFile => UseMlx ? "requirements-macos.txt" : "requirements.txt";
}
