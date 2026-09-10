using Novalist.Extensions.Speech;
using Xunit;

namespace Novalist.Extensions.Tests;

public sealed class SpeechGpuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novalist-gpu-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", "rocm")]
    [InlineData("AMD Radeon RX 9070", "rocm")]
    [InlineData("AMD Radeon(TM) RX 9060 XT", "rocm")]
    [InlineData("AMD Radeon RX 7900 XTX", "rocm")]
    [InlineData("AMD Radeon PRO W7900 Dual Slot", "rocm")]
    [InlineData("AMD Radeon RX 7900 XT", "cpu")]
    [InlineData("AMD Radeon RX 7700 XT", "cpu")]
    [InlineData("AMD Radeon RX 9070 GRE", "cpu")]
    [InlineData("AMD Radeon Graphics", "cpu")]
    [InlineData("NVIDIA GeForce RTX 4060", "cuda")]
    [InlineData("Parsec Virtual Display Adapter", "cpu")]
    public void UsesOnlySupportedWindowsAdapters(string name, string expected)
        => Assert.Equal(expected, WindowsGpu.Select([name]));

    [Fact]
    public void NvidiaWinsOnAMixedMachine()
        => Assert.Equal("cuda", WindowsGpu.Select(["AMD Radeon RX 9070 XT", "NVIDIA GeForce RTX 4060"]));

    [Theory]
    [InlineData("Python 3.12.10", true)]
    [InlineData("Python 3.13.5", false)]
    [InlineData("Python 3.11.9", false)]
    public void AmdWheelsRequirePython312(string version, bool usable)
        => Assert.Equal(usable, PythonEnvironment.IsUsable(version, rocm: true));

    [Fact]
    public void AmdMigrationPreservesTheOldEnvironmentAndInvalidatesItsMarker()
    {
        var cpu = new PythonEnvironment(_root, gpu: "cpu");
        var amd = new PythonEnvironment(_root, gpu: "rocm");
        var requirements = Requirements();
        Directory.CreateDirectory(Path.GetDirectoryName(cpu.VenvPython)!);
        File.WriteAllText(cpu.VenvPython, "old interpreter");
        File.WriteAllText(Path.Combine(_root, "installed.txt"), cpu.Recipe(requirements));
        Assert.NotEqual(cpu.VenvPath, amd.VenvPath);
        Assert.Equal(cpu.WorkPath, amd.WorkPath);
        Assert.False(amd.IsBuiltFor(requirements));
        Assert.True(cpu.IsBuiltFor(requirements));
        Assert.NotEqual(cpu.Recipe(requirements), amd.Recipe(requirements));
    }

    [Fact]
    public void LegacySuccessfulCpuInstallRequiresGpuPreparation()
    {
        var requirements = Requirements();
        var nvidia = new PythonEnvironment(_root, gpu: "cuda");
        Directory.CreateDirectory(Path.GetDirectoryName(nvidia.VenvPython)!);
        File.WriteAllText(nvidia.VenvPython, "old CPU interpreter");
        File.WriteAllText(Path.Combine(_root, "installed.txt"), Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(requirements))));
        Assert.False(nvidia.IsBuiltFor(requirements));
    }

    [Theory]
    [InlineData("cuda", 1, 0, "install-failed")]
    [InlineData("rocm", 1, 0, "install-failed")]
    [InlineData("cuda", 0, 1, "gpu-unavailable")]
    [InlineData("rocm", 0, 1, "gpu-unavailable")]
    [InlineData("cuda", 0, 0, null)]
    [InlineData("rocm", 0, 0, null)]
    public async Task PreparationChecksTheGpuAndKeepsFailuresRetryable(
        string gpu, int installExit, int probeExit, string? expected)
    {
        var calls = new List<string[]>();
        PythonEnvironment? environment = null;
        environment = new PythonEnvironment(_root, gpu: gpu, run: (executable, args, _, _) =>
        {
            calls.Add(args);
            // Linux also probes the NVIDIA driver. A successful generic GPU
            // reply here would replace the requested ROCm runtime with CUDA.
            if (executable == "nvidia-smi")
                return Task.FromResult((1, "", "NVIDIA driver not present in this fixture"));
            if (args.Contains("--version"))
                return Task.FromResult((0, "Python 3.12.10", ""));
            if (args.Any(arg => arg.Contains("struct.calcsize")))
                return Task.FromResult((0, "64", ""));
            if (args.Contains("venv"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(environment!.VenvPython)!);
                File.WriteAllText(environment.VenvPython, "interpreter");
                return Task.FromResult((0, "", ""));
            }
            if (args.Contains("pip"))
                return Task.FromResult((installExit, "pip details", installExit == 0 ? "" : "download failed"));
            if (args.SequenceEqual(new[] { "-c", PythonEnvironment.GpuProbe(gpu) }))
                return Task.FromResult((probeExit, "GPU probe", probeExit == 0 ? "" : "driver unavailable"));
            throw new InvalidOperationException($"Unexpected test command: {executable} {string.Join(' ', args)}");
        });
        var requirements = Requirements();
        var failure = await environment.BuildAsync(requirements, null);
        if (expected is null)
        {
            Assert.Null(failure);
            Assert.True(environment.IsBuiltFor(requirements));
            Assert.False(File.Exists(environment.FailurePath));
        }
        else
        {
            Assert.StartsWith(expected, failure);
            Assert.False(environment.IsBuiltFor(requirements));
            Assert.Contains(installExit != 0 ? "download failed" : "driver unavailable",
                File.ReadAllText(environment.FailurePath));
        }
        var install = Assert.Single(calls, args => args.Contains("pip"));
        Assert.Contains(requirements, install);
        if (gpu == "cuda")
        {
            Assert.Contains("torch==2.9.1+cu128", install);
            Assert.Contains("torchaudio==2.9.1+cu128", install);
        }
        else
        {
            Assert.Contains(install, arg => arg.Contains("torch-2.9.1%2Brocm7.2.1-cp312"));
            Assert.Contains(install, arg => arg.Contains("rocm_sdk_core-7.2.1"));
        }
        Assert.Equal(installExit == 0, calls.Any(args => args.Contains(PythonEnvironment.GpuProbe(gpu))));
    }

    private string Requirements()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "requirements.txt");
        File.WriteAllText(path, "qwen-tts==0.1.1");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
