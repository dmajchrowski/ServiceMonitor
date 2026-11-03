<#
.SYNOPSIS
  Builds the single, self-contained ServiceMonitor.exe that technicians copy to customer machines, and with
  -Installer also the setup program.

.DESCRIPTION
  Optional OAuth app registrations are baked into the exe so technicians only ever click "Sign in".
  See docs\OAUTH-APP-REGISTRATION.md for how to create them (one time, free).

  The version comes from Directory.Build.props unless -Version is given.

.PARAMETER Installer
  Also builds dist\ServiceMonitor-Setup-<version>.exe with Inno Setup 6
  (winget install JRSoftware.InnoSetup).

.EXAMPLE
  .\build\publish.ps1 -Installer
.EXAMPLE
  .\build\publish.ps1 -MicrosoftClientId 11111111-2222-3333-4444-555555555555
#>
param(
    [string]$MicrosoftClientId = "",
    [string]$MicrosoftTenant = "",
    [string]$GoogleClientId = "",
    [string]$GoogleClientSecret = "",
    [string]$Version = "",
    [string]$Output = (Join-Path $PSScriptRoot "..\dist"),
    [switch]$Installer
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version }

dotnet test (Join-Path $root "tests\ServiceMonitor.Core.Tests") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }

dotnet publish (Join-Path $root "src\ServiceMonitor") -c Release -o $Output --nologo `
    "-p:Version=$Version" `
    "-p:MicrosoftClientId=$MicrosoftClientId" `
    "-p:MicrosoftTenant=$MicrosoftTenant" `
    "-p:GoogleClientId=$GoogleClientId" `
    "-p:GoogleClientSecret=$GoogleClientSecret"
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Get-ChildItem $Output -Filter *.pdb | Remove-Item

# The exe bundles the .NET runtime and NuGet packages such as MailKit. Ship their license and notice texts next to
# it, taken from the exact versions this publish restored.
$assets = Get-Content (Join-Path $root "src\ServiceMonitor\obj\project.assets.json") -Raw | ConvertFrom-Json
$packages = @($assets.packageFolders.PSObject.Properties.Name)[0]
$sources = @()
foreach ($fw in $assets.project.frameworks.PSObject.Properties) {
    # Only the runtimes the app uses (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App.WPF): the SDK also downloads
    # ASP.NET Core's, which is not in the exe.
    $refs = @($fw.Value.frameworkReferences.PSObject.Properties.Name)
    foreach ($d in $fw.Value.downloadDependencies) {
        if ($d.name -notlike '*.Runtime.win-x64') { continue }
        $base = $d.name -replace '\.Runtime\.win-x64$', ''
        if (-not ($refs | Where-Object { $_ -eq $base -or $_ -like "$base.*" })) { continue }
        $v = $d.version -replace '^\[([^,\]]+).*$', '$1' # "[10.0.12, 10.0.12]"
        $sources += @{ Name = "$($d.name) $v"; Dir = Join-Path $packages "$($d.name.ToLowerInvariant())/$v" }
    }
}
$shipped = ($assets.targets.PSObject.Properties | Where-Object Name -like '*/win-x64' | Select-Object -First 1).Value
foreach ($lib in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    $t = $shipped.($lib.Name)
    if ($lib.Value.type -ne 'package' -or -not ($t.runtime -or $t.native -or $t.runtimeTargets)) { continue } # build-only
    $sources += @{ Name = $lib.Name -replace '/', ' '; Dir = Join-Path $packages $lib.Value.path }
}
$mit = @"
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@ -replace "`r`n", "`n"
$texts = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal) # text -> files that contain it
foreach ($s in $sources) {
    $files = @(Get-ChildItem $s.Dir -File | Where-Object Name -match '^(license|notice|third-?party-?notices)' | Sort-Object Name)
    foreach ($f in $files) {
        $text = ([IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n").Trim()
        $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - $($f.Name)"
    }
    if ($files | Where-Object Name -match '^license') { continue }
    # No license file in the package (MailKit, MimeKit, Microsoft.Extensions.*): use the license its .nuspec declares.
    $meta = ([xml](Get-Content (Get-ChildItem $s.Dir -Filter *.nuspec | Select-Object -First 1).FullName)).package.metadata
    $expression = if ($meta.license.type -eq 'expression') { $meta.license.'#text' } else { '' }
    $copyright = "Copyright (c) " + ($meta.copyright -replace '^(Copyright\s*)?(©|\(c\))?\s*', '')
    $text = if ($expression -eq 'MIT') { "MIT License`n`n$copyright`n`n$mit".Trim() }
            else { Write-Warning "$($s.Name): no license file and license '$expression' - check its terms by hand."; "$copyright`n`nLicensed under $expression ($($meta.projectUrl))." }
    $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - license declared in the package ($expression)"
}
$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine("ServiceMonitor.exe includes the following third-party software. Their license and notice texts follow.")
foreach ($t in $texts.GetEnumerator()) {
    [void]$notices.AppendLine("`n$('=' * 78)`n$($t.Value -join "`n")`n$('=' * 78)`n`n$($t.Key)")
}
[IO.File]::WriteAllText((Join-Path $Output "THIRD-PARTY-NOTICES.txt"), ($notices.ToString() -replace "`r?`n", "`r`n"))
Copy-Item (Join-Path $root "LICENSE") (Join-Path $Output "LICENSE.txt")

$exe = Join-Path $Output "ServiceMonitor.exe"
"{0} ({1:N1} MB), version {2}" -f $exe, ((Get-Item $exe).Length / 1MB), $Version
if (-not $MicrosoftClientId) { Write-Host "No Microsoft client ID baked in: the Outlook.com option stays hidden until one is entered under Settings > Advanced (Gmail, Yahoo and iCloud work with app passwords)." }
if (-not $GoogleClientId) { Write-Host "No Google OAuth client baked in: Gmail will use app passwords (works fine)." }

if ($Installer) {
    $iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup)." }
    $out = (Resolve-Path $Output).Path
    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$out" "/O$out" (Join-Path $root "installer\ServiceMonitor.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    $setup = Join-Path $out "ServiceMonitor-Setup-$Version.exe"
    "{0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)
}
