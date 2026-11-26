using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lockstep.Core;

public sealed record AppNetworkConnection(int ProcessId, string App, string? Path, string Address, int Port)
{
    public string Display => $"{App} (PID {ProcessId})  ·  [{Address}]:{Port}\n{Path ?? "Executable path unavailable"}";
}

public sealed record AppNetworkBlockRule(string Name, string Path, bool Enabled)
{
    public string Display => Enabled ? Path : $"{Path} (disabled)";
}

public static class AppNetwork
{
    private const string Group = "Lockstep App Network";
    private const string Prefix = "Lockstep.AppNetwork.";

    public static IReadOnlyList<AppNetworkConnection> GetConnections() => Read<AppNetworkConnection>("""
        $processes = @{}
        Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $processes[[int]$_.Id] = $_ }
        $rows = @(Get-NetTCPConnection | Where-Object State -eq 'Established' | ForEach-Object {
            $p = $processes[[int]$_.OwningProcess]
            $path = $null
            if ($p -and $p.StartTime -and $_.CreationTime -and $p.StartTime -le $_.CreationTime) { $path = $p.Path }
            [pscustomobject]@{
                ProcessId = $_.OwningProcess
                App = if ($path) { $p.ProcessName } else { 'Unknown process' }
                Path = $path
                Address = $_.RemoteAddress
                Port = $_.RemotePort
            }
        })
        ConvertTo-Json -InputObject $rows -Compress
        """).OrderBy(x => x.App, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.ProcessId).ThenBy(x => x.Address).ThenBy(x => x.Port).ToArray();

    public static IReadOnlyList<AppNetworkBlockRule> GetRules() => Read<AppNetworkBlockRule>($$"""
        $rows = @(Get-NetFirewallRule -PolicyStore PersistentStore |
            Where-Object { $_.Group -eq '{{Group}}' -and $_.Name -like '{{Prefix}}*' } | ForEach-Object {
                $filter = $_ | Get-NetFirewallApplicationFilter
                [pscustomobject]@{ Name = $_.Name; Path = $filter.Program; Enabled = $_.Enabled -eq 'True' }
            })
        ConvertTo-Json -InputObject $rows -Compress
        """).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static void Block(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("The executable is unavailable.");

        var name = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        // Base64 carries the path as data, even if a filename contains PowerShell metacharacters.
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        Run($$"""
            $path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedPath}}'))
            $rule = Get-NetFirewallRule -PolicyStore PersistentStore -Name '{{name}}' -ErrorAction SilentlyContinue
            if ($rule) {
                if ($rule.Group -ne '{{Group}}') { throw 'The firewall rule name is already in use.' }
                $rule | Enable-NetFirewallRule
            } else {
                New-NetFirewallRule -PolicyStore PersistentStore -Name '{{name}}' -DisplayName ('Lockstep app block: ' + [IO.Path]::GetFileName($path)) -Group '{{Group}}' -Program $path -Direction Outbound -Action Block -Profile Any | Out-Null
            }
            """);
    }

    public static void Remove(AppNetworkBlockRule rule)
    {
        if (!rule.Name.StartsWith(Prefix, StringComparison.Ordinal) ||
            rule.Name.Length != Prefix.Length + 64 || !rule.Name[Prefix.Length..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The selected rule is outside Lockstep's app network rules.");

        Run($$"""
            Get-NetFirewallRule -PolicyStore PersistentStore |
                Where-Object { $_.Name -eq '{{rule.Name}}' -and $_.Group -eq '{{Group}}' } | Remove-NetFirewallRule
            """);
    }

    private static T[] Read<T>(string script) =>
        JsonSerializer.Deserialize<T[]>(Run(script), AppData.Json) ?? [];

    private static string Run(string script)
    {
        var result = ProcessRunner.PowerShell("$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script,
            timeoutMs: 30_000, outputEncoding: Encoding.UTF8);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return result.Output;
    }
}
