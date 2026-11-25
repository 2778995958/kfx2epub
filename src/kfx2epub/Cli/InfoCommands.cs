using System.Reflection;
using System.Runtime.InteropServices;

namespace Kfx2Epub.Cli;

public static class InfoCommands
{
    public static Dictionary<string, object> VersionInfo()
    {
        var assembly = typeof(InfoCommands).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;
        var commit = plus >= 0 ? informational[(plus + 1)..] : null;

        return new Dictionary<string, object>
        {
            ["api"] = CliContract.Api,
            ["tool"] = "kfx2epub",
            ["version"] = version,
            ["commit"] = string.IsNullOrEmpty(commit) ? null : commit,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["platform"] = Platform()
        };
    }

    public static Dictionary<string, object> Capabilities()
    {
        return new Dictionary<string, object>
        {
            ["api"] = CliContract.Api,
            ["input_formats"] = new List<object> { "kfx", "kfx-zip" },
            ["output_formats"] = new List<object> { "epub3", "epub2" },
            ["probe"] = true,
            // 尚未實作 NDJSON 進度串流；必須誠實回報 false。
            ["progress_ndjson"] = false,
            ["reproducible"] = true,
            ["pdf_backed"] = "pass-through",
            ["dictionary"] = false,
            ["drm"] = "reject",
            ["max_input_bytes"] = 2147483591,
            ["platform"] = Platform()
        };
    }

    public static string Platform()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrEmpty(rid)) return rid;

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }
}
