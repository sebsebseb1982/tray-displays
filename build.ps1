# Compilation avec le compilateur C# in-box (.NET Framework 4.x), sans SDK ni NuGet.
# -Version : version semver (ex. 1.2.3) inscrite dans l'exe ; utilisée par la CI de release.
param(
    [string]$Version = '0.0.0'
)

$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $root 'bin'
$out = Join-Path $bin 'TrayDisplays.exe'
$manifest = Join-Path $root 'app.manifest'

if (-not (Test-Path $csc)) {
    throw "Compilateur introuvable : $csc"
}

New-Item -ItemType Directory -Force -Path $bin | Out-Null

$sources = @(Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "Aucun fichier .cs dans src\"
}

# Version dans les propriétés de l'exe (partie numérique x.y.z uniquement pour AssemblyVersion).
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
    throw "Version invalide : $Version (attendu x.y.z)"
}
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null
$assemblyInfo = Join-Path $obj 'AssemblyInfo.cs'
@(
    'using System.Reflection;',
    '[assembly: AssemblyTitle("Tray Displays")]',
    "[assembly: AssemblyVersion(""$numeric"")]",
    "[assembly: AssemblyFileVersion(""$numeric"")]",
    "[assembly: AssemblyInformationalVersion(""$Version"")]"
) | Set-Content -Path $assemblyInfo -Encoding UTF8
$sources += $assemblyInfo

# NB : les valeurs sont calculées avant le tableau, sinon '+' devient une concaténation de tableaux.
$options = @(
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/nologo',
    ('/win32manifest:' + $manifest),
    ('/out:' + $out),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)

& $csc ($options + $sources)
if ($LASTEXITCODE -ne 0) {
    throw "Compilation échouée (exit $LASTEXITCODE)"
}

Write-Host "OK : $out"
