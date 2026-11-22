param(
    [switch]$Test,
    [switch]$Package,
    [string]$Dotnet = 'dotnet',
    [string]$Libmpv,
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
if (!$Package) { return }

$cache = Join-Path (Join-Path $repo '.tools') 'FilmPick'
$stage = Join-Path $artifacts ('FilmPick-stage-' + [guid]::NewGuid().ToString('N'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $project 'FilmPick.csproj') -Raw)).Project.PropertyGroup.Version
$portable = Join-Path $stage "FilmPick-$version-win-x64"
$licenses = Join-Path $portable 'licenses'
New-Item -ItemType Directory -Force $cache,$licenses | Out-Null
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

& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $portable --nologo
CheckExit 'FilmPick publish'
Copy-Item -LiteralPath $Libmpv -Destination (Join-Path $portable 'libmpv-2.dll')
& (Join-Path $PSScriptRoot 'package-raw.ps1') -Cache (Join-Path $cache '1.2/mingw') -Portable $portable
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
$metadata = Join-Path $portable 'metadata'
Move-Item -LiteralPath (Join-Path $stage 'exiftool-13.64_64') -Destination $metadata
Rename-Item -LiteralPath (Join-Path $metadata 'exiftool(-k).exe') -NewName 'exiftool.exe'
if ($BenchmarkReport) { Copy-Item -LiteralPath $BenchmarkReport -Destination (Join-Path $portable 'benchmark.json') }

if ($LicenseDirectory) {
    Get-ChildItem -LiteralPath $LicenseDirectory -File | Copy-Item -Destination $licenses
} else {
    $runtime = ([IO.File]::ReadAllText((Join-Path $portable 'FilmPick.deps.json')) | ConvertFrom-Json).libraries.PSObject.Properties.Name |
        Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' } |
        ForEach-Object { ($_ -split '/')[-1] } | Select-Object -First 1
    if (!$runtime) { throw 'Unable to identify the published .NET runtime version.' }
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

$docs = @('README.zh-CN.md','THIRD-PARTY.md') | ForEach-Object { Join-Path $PSScriptRoot $_ }
Copy-Item -LiteralPath $docs -Destination $portable
$name = Split-Path $portable -Leaf
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
