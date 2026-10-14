<#
.SYNOPSIS
  Builds the Microsoft Store (MSIX) package. Run from the repository root:
    pwsh tools/Msix/build-msix.ps1 -Version 0.1.0-alpha.5

.DESCRIPTION
  Publishes the app as a folder (not single-file: a single-file exe unpacks native libraries into TEMP,
  which packaged apps see redirected), adds the manifest and Store logos from packaging/msix, builds the
  resource index and packs an unsigned .msix. The Store signs it after certification.

  The Store needs four-part versions ending in .0 that always increase, so semantic versions map to
  Major.Minor.(Patch*1000 + stage).0 with stage alpha.N = N, beta.N = 300+N, rc.N = 600+N, release = 999.
  Example: 0.1.0-alpha.5 -> 0.1.5.0, 0.1.0 -> 0.1.999.0, 0.1.1-beta.2 -> 0.1.1302.0.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDirectory = 'artifacts/msix'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '../..')

function ConvertTo-StoreVersion([string] $semver) {
    if ($semver -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?$') {
        throw "Version '$semver' must look like 1.2.3 or 1.2.3-alpha.4 (alpha, beta or rc)."
    }
    $stage = switch ($Matches[4]) { 'alpha' { 0 } 'beta' { 300 } 'rc' { 600 } default { $null } }
    $number = if ($null -eq $stage) { 999 } else { $stage + [int]$Matches[5] }
    if ($null -ne $stage -and [int]$Matches[5] -gt 299) { throw "Pre-release number too large for '$semver'." }
    $build = [int]$Matches[3] * 1000 + $number
    if ($build -gt 65535) { throw "Patch number too large for a Store version: '$semver'." }
    "$($Matches[1]).$($Matches[2]).$build.0"
}

$storeVersion = ConvertTo-StoreVersion $Version
Write-Host "Version $Version -> Store package version $storeVersion"

# Windows SDK packaging tools from the pinned NuGet package (see Msix.proj)
dotnet restore (Join-Path $repo 'tools/Msix/Msix.proj') --nologo -v q
if ($LASTEXITCODE) { throw 'Restoring the packaging tools failed.' }
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$sdkBin = Get-ChildItem (Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools') -Recurse -Directory -Filter x64 |
    Where-Object { Test-Path (Join-Path $_.FullName 'makeappx.exe') } | Select-Object -First 1
if (-not $sdkBin) { throw 'makeappx.exe not found in Microsoft.Windows.SDK.BuildTools.' }
$makeappx = Join-Path $sdkBin.FullName 'makeappx.exe'
$makepri = Join-Path $sdkBin.FullName 'makepri.exe'

$work = Join-Path $repo 'artifacts/msix-work'
$layout = Join-Path $work 'layout'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

# 1. The app, as a folder of files
dotnet publish (Join-Path $repo 'src/VersePreflight.App') -p:PublishProfile=win-x64 -p:PublishSingleFile=false `
    "-p:Version=$Version" -o $layout --nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
Get-ChildItem $layout -Filter *.pdb | Remove-Item # debug symbols aren't shipped

# 2. Manifest (with this build's version) and logos
$manifest = Get-Content (Join-Path $repo 'packaging/msix/AppxManifest.xml') -Raw
$manifest = $manifest -replace '(<Identity[^>]*?\sVersion=")0\.0\.0\.0"', "`${1}$storeVersion`""
if ($manifest -notmatch [regex]::Escape("Version=`"$storeVersion`"")) { throw 'Could not set the version in AppxManifest.xml.' }
Set-Content (Join-Path $layout 'AppxManifest.xml') $manifest -NoNewline -Encoding utf8
Copy-Item (Join-Path $repo 'packaging/msix/Assets') (Join-Path $layout 'Assets') -Recurse

# 3. Resource index, so Windows can pick the right logo size for the taskbar, Start and Explorer
$priConfig = Join-Path $work 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri createconfig failed.' }
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri new failed.' }

# 4. Pack
$outDir = Join-Path $repo $OutputDirectory
New-Item -ItemType Directory -Force $outDir | Out-Null
$package = Join-Path $outDir "VersePreflight-$Version-x64.msix"
& $makeappx pack /d $layout /p $package /o | Out-Null
if ($LASTEXITCODE) { throw 'makeappx pack failed.' }

Write-Host "Built $package ($([math]::Round((Get-Item $package).Length / 1MB, 1)) MB)"
