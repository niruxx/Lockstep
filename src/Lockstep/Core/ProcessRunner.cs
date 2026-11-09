using System.Diagnostics;
using System.Text;

namespace Lockstep.Core;

public readonly record struct ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Message => string.IsNullOrWhiteSpace(Error) ? Output.Trim() : Error.Trim();
}

public static class ProcessRunner
{
    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static ProcessResult Run(string fileName, string arguments, int timeoutMs = 60_000, Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ProcessResult(-1, "", $"{Path.GetFileName(fileName)} timed out");
        }

        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Runs a PowerShell script, base64-encoded so quoting can never break it.</summary>
    public static ProcessResult PowerShell(string script, int timeoutMs = 300_000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run(System32(@"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs);
    }
}
