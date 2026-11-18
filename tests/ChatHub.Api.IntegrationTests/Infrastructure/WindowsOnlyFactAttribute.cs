using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows: denies folder access with icacls.";
    }
}

[SupportedOSPlatform("windows")]
public sealed class DeniedWriteAccess : IDisposable
{
    private readonly string _folder;
    private readonly string _sid = "*" + WindowsIdentity.GetCurrent().User!.Value;

    public DeniedWriteAccess(string folder)
    {
        _folder = folder;
        RunIcacls($"\"{_folder}\" /deny {_sid}:(W)");
    }

    public void Dispose() => RunIcacls($"\"{_folder}\" /remove:d {_sid}");

    private static void RunIcacls(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"icacls {arguments} failed: {output}");
    }
}
