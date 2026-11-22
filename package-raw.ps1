param(
    [Parameter(Mandatory)][string]$Cache,
    [Parameter(Mandatory)][string]$Portable
)

$ErrorActionPreference = 'Stop'
$packages = @(
    @('libraw-0.22.3-1','libraw_r-25.dll','4C61C19F59E10D2A625BD042074C113F7B4454BF82B7353DEE74DAECD7035FDB'),
    @('lcms2-2.18-1','liblcms2-2.dll','72ADB0115106FB8D329D2A4D83711316BD12D6BEF749FF01918A59D11A095BC7'),
    @('libgcc-16.2.0-7','libgcc_s_seh-1.dll','BC0AE31C582E0847964B4C8C4B55BB2404A9B7DDCE7EBF0B176B6693B76E0C62'),
    @('libgomp-16.2.0-7','libgomp-1.dll','CA3C10421830F19247C701BEC9A2AC8F3D6514685C8C9D1733FD5C89FA2DA3EF'),
    @('libjpeg-turbo-3.1.4.1-3','libjpeg-8.dll','066CA64FDDB32AC9876953843EBF2552157F3A05D95DE099FEFCFB782BEABA01'),
    @('libstdc++-16.2.0-7','libstdc++-6.dll','4DD4B216B0E92FFA160127DD50620372609E98CB219FF1C5DCB9B3FBFE153253'),
    @('libwinpthread-14.0.0.r598.ge3d18511f-1','libwinpthread-1.dll','C35D6BE83D141C8CFBB61F1637DD4B53AAC8F5562D766AA26D0741C40BAD4E4D'),
    @('zlib-1.4-2','zlib1.dll','172541CF5E2391A402F092104988699B0163129D22BE1D6B4B201BFB84AF6FCF')
)
$extract = Join-Path $Cache ('extract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $extract | Out-Null
foreach ($package in $packages) {
    $version, $dll, $sha = $package
    $name = "mingw-w64-ucrt-x86_64-$version-any.pkg.tar.zst"
    $archive = Join-Path $Cache $name
    if (!(Test-Path -LiteralPath $archive)) {
        & curl.exe -fLsS --retry 2 "https://repo.msys2.org/mingw/ucrt64/$name" -o $archive
        if ($LASTEXITCODE) { throw "Download failed: $name" }
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $sha) { throw "RAW dependency checksum mismatch: $name" }
    $license = @(& tar.exe -tf $archive | Where-Object { $_ -match '^ucrt64/share/licenses/[^/]+/[^/]+$' })
    if ($LASTEXITCODE -or $license.Count -eq 0) { throw "Missing dependency licenses: $name" }
    & tar.exe -xf $archive -C $extract "ucrt64/bin/$dll" @license
    if ($LASTEXITCODE) { throw "RAW dependency extraction failed: $name" }
    Copy-Item -LiteralPath (Join-Path $extract "ucrt64/bin/$dll") -Destination $Portable
}
$notices = Join-Path $Portable 'licenses/RAW-dependencies'
Copy-Item -LiteralPath (Join-Path $extract 'ucrt64/share/licenses') -Destination $notices -Recurse
