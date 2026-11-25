param(
    [switch]$Test,
    [switch]$Package,
    [switch]$SingleExe,
    [string]$Dotnet = 'dotnet',
    [string]$Libmpv,
    [string]$PlayerBundle,
    [string]$LicenseDirectory,
    [string]$BenchmarkReport
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$project = Join-Path $PSScriptRoot 'FilmPick'
$tests = Join-Path $PSScriptRoot 'FilmPick.Tests'
$archiveHash = '03e2a563417f0b335be03c044ce092977099c96498e6c7f60a44812b2c46d663'
$libraryHash = '214b93229e851764e7a3909ad9d4829a8b286d9434ad0878717466d6944dbb3d'

function CheckExit([string]$Activity) {
    if ($LASTEXITCODE -ne 0) { throw "$Activity failed ($LASTEXITCODE)." }
}

function Download([string]$Uri, [string]$Target) {
    & curl.exe -fLsS --retry 2 $Uri -o $Target
    CheckExit 'Download'
}

& $Dotnet build $project -c Release --nologo
CheckExit 'FilmPick build'
if ($Test) {
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    & $Dotnet run --project $tests -c Release -- $artifacts
    CheckExit 'FilmPick tests'
}
if (!$Package -and !$SingleExe) { return }

$cache = Join-Path (Join-Path $repo '.tools') 'FilmPick'
$stage = Join-Path $artifacts ('FilmPick-stage-' + [guid]::NewGuid().ToString('N'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $project 'FilmPick.csproj') -Raw)).Project.PropertyGroup.Version
$portable = Join-Path $stage "FilmPick-$version-win-x64"
$support = Join-Path $stage 'support'
$licenses = Join-Path $support 'licenses'
New-Item -ItemType Directory -Force $cache,$licenses | Out-Null
if (!$PlayerBundle -and !$Libmpv) {
    $localPlayer = Join-Path $cache 'player-no-vulkan'
    if (Test-Path -LiteralPath (Join-Path $localPlayer 'SHA256SUMS.txt')) { $PlayerBundle = $localPlayer }
}
if ($SingleExe -and !$PlayerBundle) { throw 'Build and stage the no-Vulkan player, then pass -PlayerBundle.' }
if ($PlayerBundle) {
    if ($Libmpv) { throw 'Choose a verified player bundle or the legacy libmpv DLL.' }
    $PlayerBundle = (Resolve-Path -LiteralPath $PlayerBundle).Path
    $nativeFiles = @(Get-ChildItem -File -LiteralPath $PlayerBundle -Filter '*.dll')
    $expected = @{}
    foreach ($line in (Get-Content -LiteralPath (Join-Path $PlayerBundle 'SHA256SUMS.txt'))) {
        if ($line -notmatch '^([0-9A-Fa-f]{64}) [ *]([A-Za-z0-9_.+-]+[.]dll)$') { throw "Invalid native manifest entry: $line" }
        $expected[$Matches[2]] = $Matches[1]
    }
    if ($expected.Count -ne $nativeFiles.Count -or !$expected.ContainsKey('libmpv-2.dll') -or $expected.ContainsKey('vulkan-1.dll')) {
        throw 'Invalid no-Vulkan player manifest.'
    }
    foreach ($dll in $nativeFiles) {
        if ((Get-FileHash -LiteralPath $dll.FullName).Hash -ne $expected[$dll.Name]) { throw "Player dependency checksum mismatch: $($dll.Name)" }
    }
} else {
    if (!$Libmpv) {
        $Libmpv = Join-Path $cache 'libmpv-2.dll'
        if (!(Test-Path -LiteralPath $Libmpv)) {
            $asset = Join-Path $cache 'mpv.7z'
            Download 'https://github.com/zhongfly/mpv-winbuild/releases/download/2026-11-10-d075ec2a28/mpv-dev-lgpl-x86_64-20261110-git-d075ec2a28.7z' $asset
            if ((Get-FileHash -LiteralPath $asset).Hash -ne $archiveHash) { throw 'libmpv release checksum mismatch.' }
            & tar.exe -xf $asset -C $cache libmpv-2.dll
            CheckExit 'libmpv extraction'
        }
    }
    if ((Get-FileHash -LiteralPath $Libmpv).Hash -ne $libraryHash) { throw 'libmpv DLL checksum mismatch.' }
}

& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $portable --nologo
CheckExit 'FilmPick publish'
& (Join-Path $PSScriptRoot 'package-raw.ps1') -Cache (Join-Path $cache '1.2/mingw') -Portable $support
if ($PlayerBundle) {
    foreach ($dll in $nativeFiles) { Copy-Item -LiteralPath $dll.FullName -Destination $support -Force }
    Copy-Item -LiteralPath (Join-Path $PlayerBundle 'player-notices') -Destination $licenses -Recurse
} else {
    Copy-Item -LiteralPath $Libmpv -Destination (Join-Path $support 'libmpv-2.dll')
}
$metadataCache = Join-Path $cache '1.3'
New-Item -ItemType Directory -Force $metadataCache | Out-Null
$exifZip = Join-Path $metadataCache 'exiftool-13.64_64.zip'
if (!(Test-Path -LiteralPath $exifZip)) {
    Download 'https://downloads.sourceforge.net/project/exiftool/exiftool-13.64_64.zip' $exifZip
}
if ((Get-FileHash -LiteralPath $exifZip).Hash -ne '72C29C44D1E860EEBCAEFA71692D979AF9858FE9A6D7D791A5E7F1DAC77804AF') {
    throw 'ExifTool release checksum mismatch.'
}
& tar.exe -xf $exifZip -C $stage
CheckExit 'ExifTool extraction'
$metadata = Join-Path $support 'metadata'
Move-Item -LiteralPath (Join-Path $stage 'exiftool-13.64_64') -Destination $metadata
Rename-Item -LiteralPath (Join-Path $metadata 'exiftool(-k).exe') -NewName 'exiftool.exe'
if ($BenchmarkReport) { Copy-Item -LiteralPath $BenchmarkReport -Destination (Join-Path $support 'benchmark.json') }

if ($LicenseDirectory) {
    Get-ChildItem -LiteralPath $LicenseDirectory -File | Copy-Item -Destination $licenses
} else {
    $runtime = ([IO.File]::ReadAllText((Join-Path $portable 'FilmPick.deps.json')) | ConvertFrom-Json).libraries.PSObject.Properties.Name |
        Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' } |
        ForEach-Object { ($_ -split '/')[-1] } | Select-Object -First 1
    if (!$runtime) { throw 'Unable to identify the published .NET runtime version.' }
    if ($PlayerBundle) {
        $packages = $env:NUGET_PACKAGES
        if (!$packages) { $packages = Join-Path $env:USERPROFILE '.nuget/packages' }
        $coreRuntime = Join-Path $packages "microsoft.netcore.app.runtime.win-x64/$runtime"
        $desktopRuntime = Join-Path $packages "microsoft.windowsdesktop.app.runtime.win-x64/$runtime"
        Copy-Item -LiteralPath (Join-Path $coreRuntime 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-runtime-LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $coreRuntime 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-runtime-THIRD-PARTY-NOTICES.txt')
        Copy-Item -LiteralPath (Join-Path $desktopRuntime 'LICENSE') -Destination (Join-Path $licenses 'dotnet-desktop-LICENSE.txt')
        Download "https://raw.githubusercontent.com/dotnet/wpf/v$runtime/THIRD-PARTY-NOTICES.TXT" (Join-Path $licenses 'dotnet-wpf-THIRD-PARTY-NOTICES.txt')
    } else {
        $notices = @{
            'libmpv-LGPL-2.1.txt' = 'https://raw.githubusercontent.com/mpv-player/mpv/d075ec2a28/LICENSE.LGPL'
            'libmpv-Copyright.txt' = 'https://raw.githubusercontent.com/mpv-player/mpv/d075ec2a28/Copyright'
            'FFmpeg-LGPL-3.txt' = 'https://raw.githubusercontent.com/FFmpeg/FFmpeg/49a3b1f25/COPYING.LGPLv3'
            'FFmpeg-GPL-3.txt' = 'https://raw.githubusercontent.com/FFmpeg/FFmpeg/49a3b1f25/COPYING.GPLv3'
            'FFmpeg-LICENSE.md' = 'https://raw.githubusercontent.com/FFmpeg/FFmpeg/49a3b1f25/LICENSE.md'
            'dotnet-runtime-LICENSE.txt' = "https://raw.githubusercontent.com/dotnet/runtime/v$runtime/LICENSE.TXT"
            'dotnet-runtime-THIRD-PARTY-NOTICES.txt' = "https://raw.githubusercontent.com/dotnet/runtime/v$runtime/THIRD-PARTY-NOTICES.TXT"
            'dotnet-desktop-LICENSE.txt' = "https://raw.githubusercontent.com/dotnet/wpf/v$runtime/LICENSE"
            'dotnet-wpf-THIRD-PARTY-NOTICES.txt' = "https://raw.githubusercontent.com/dotnet/wpf/v$runtime/THIRD-PARTY-NOTICES.TXT"
        }
        foreach ($notice in $notices.GetEnumerator()) { Download $notice.Value (Join-Path $licenses $notice.Key) }
    }
}

$docs = @('README.zh-CN.md','THIRD-PARTY.md') | ForEach-Object { Join-Path $PSScriptRoot $_ }
Copy-Item -LiteralPath $docs -Destination $support
$name = Split-Path $portable -Leaf
if ($SingleExe) {
    $single = Join-Path $stage 'single'
    & $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false `
        -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        "-p:FilmPickPackageContent=$support" -o $single --nologo
    CheckExit 'FilmPick single EXE publish'
    $contents = @(Get-ChildItem -LiteralPath $single -Force)
    if ($contents.Count -ne 1 -or $contents[0].Name -ne 'FilmPick.exe') {
        throw 'Single EXE publish produced extra files or no executable.'
    }
    $exe = Join-Path $artifacts ($name + '.exe')
    Move-Item -LiteralPath $contents[0].FullName -Destination $exe -Force
    Write-Output $exe
}
if (!$Package) { return }

Get-ChildItem -LiteralPath $support -Force | Copy-Item -Destination $portable -Recurse
$bundle = Join-Path $stage ($name + '.7z')
$sevenZip = Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (!$sevenZip) {
    $candidate = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
    if (Test-Path -LiteralPath $candidate) { $sevenZip = $candidate }
}
if ($sevenZip) {
    Push-Location $stage
    try { & $sevenZip a -t7z -m0=lzma2 -mx=5 -mmt=4 -ms=on -bso0 -bsp0 $bundle $name }
    finally { Pop-Location }
} else {
    & tar.exe -cf $bundle --format 7zip --options '7zip:compression=lzma2,7zip:compression-level=3' -C $stage $name
}
CheckExit '7z creation'
$output = Join-Path $artifacts ($name + '.7z')
Move-Item -LiteralPath $bundle -Destination $output -Force
Write-Output $output
