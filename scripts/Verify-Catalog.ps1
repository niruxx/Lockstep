<#
.SYNOPSIS
    Checks every package ID in Lockstep's catalogs against the real repositories.

.DESCRIPTION
    Reads the winget / Microsoft Store / Chocolatey IDs from Installer.cs (Install page) and
    the Store IDs from AppPackages.cs (optional Windows apps), then asks winget and Chocolatey
    whether each one exists. Prints a table and exits with 1 if any ID is unknown.
    Needs winget; Chocolatey checks are skipped if choco isn't installed. Read-only.

.EXAMPLE
    .\scripts\Verify-Catalog.ps1
#>
[CmdletBinding()]
param(
    [string]$Root
)

if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }

$installer = Get-Content -Raw (Join-Path $Root 'src\Lockstep\Core\Installer.cs')
$packages  = Get-Content -Raw (Join-Path $Root 'src\Lockstep\Core\AppPackages.cs')

$winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
if (-not (Test-Path $winget)) { throw 'winget is required.' }
$choco = Join-Path $env:ProgramData 'chocolatey\bin\choco.exe'
$hasChoco = Test-Path $choco

$checks = [System.Collections.Generic.List[object]]::new()

# Install catalog: new("Name", "Category", "Description", "WingetId", "chocoId" | null[, "msstore"])
$catalog = [regex]::Matches($installer,
    'new\("(?<name>[^"]+)", "(?<cat>[^"]+)", "(?:[^"\\]|\\.)*", "(?<wid>[^"]+)", (?:"(?<cid>[^"]+)"|null)(?:, "(?<src>[^"]+)")?\)')
foreach ($m in $catalog) {
    $source = if ($m.Groups['src'].Success) { $m.Groups['src'].Value } else { 'winget' }
    $checks.Add([pscustomobject]@{ Where = 'Install'; Name = $m.Groups['name'].Value; Manager = $source; Id = $m.Groups['wid'].Value })
    if ($m.Groups['cid'].Success) {
        $checks.Add([pscustomobject]@{ Where = 'Install'; Name = $m.Groups['name'].Value; Manager = 'choco'; Id = $m.Groups['cid'].Value })
    }
}

# Optional Windows apps: new("Name", "Package.Name", "STOREID", "Description")
$optional = [regex]::Matches($packages, 'new\("(?<name>[^"]+)", "(?<pkg>[A-Za-z0-9.]+)", "(?<sid>[0-9A-Z]{12})", ')
foreach ($m in $optional) {
    $checks.Add([pscustomobject]@{ Where = 'Optional apps'; Name = $m.Groups['name'].Value; Manager = 'msstore'; Id = $m.Groups['sid'].Value })
}

Write-Host "Checking $($checks.Count) IDs..." -ForegroundColor Cyan
$results = foreach ($c in $checks) {
    $ok = switch ($c.Manager) {
        'choco' {
            if (-not $hasChoco) { $null; break }
            $out = & $choco search $c.Id --exact --limit-output 2>$null
            [bool]($out | Where-Object { $_ -like "$($c.Id)|*" })
        }
        default {
            & $winget show --id $c.Id --exact --source $c.Manager --accept-source-agreements --disable-interactivity *> $null
            $LASTEXITCODE -eq 0
        }
    }
    $status = if ($null -eq $ok) { 'skipped' } elseif ($ok) { 'ok' } else { 'NOT FOUND' }
    $c | Add-Member -NotePropertyName Status -NotePropertyValue $status -PassThru
}

$results | Sort-Object Status, Where, Name | Format-Table Status, Where, Name, Manager, Id -AutoSize | Out-String -Width 200 | Write-Host
$bad = @($results | Where-Object Status -eq 'NOT FOUND')
Write-Host "$($results.Count) checked, $(@($results | Where-Object Status -eq 'ok').Count) ok, $($bad.Count) not found." -ForegroundColor ($(if ($bad.Count) { 'Yellow' } else { 'Green' }))
exit ([int]($bad.Count -gt 0))
