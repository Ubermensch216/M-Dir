<#
.SYNOPSIS
  Builds the release package of M-Dir: one self-contained M-Dir.exe (no installed .NET needed) and a portable zip.

.DESCRIPTION
  artifacts\M-Dir\M-Dir.exe              the program
  artifacts\M-Dir-<version>-<rid>.zip    portable package: M-Dir.exe + an empty M-Dir.ini (its presence next to the
                                         exe makes M-Dir keep all settings, favorites and workspaces beside the exe
                                         and leave the registry and %APPDATA% alone) + Lang\README.txt

  Run from the repository root:  .\publish.ps1   (or  .\publish.ps1 -Runtime win-arm64)
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MDir\MDir.csproj'
$publishDir = Join-Path $Output 'M-Dir'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r $Runtime -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Portable package: a staging folder so the plain exe above stays free of the marker file.
$staging = Join-Path $Output 'portable'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory $staging | Out-Null
New-Item -ItemType Directory (Join-Path $staging 'Lang') | Out-Null
Copy-Item (Join-Path $publishDir 'M-Dir.exe') $staging
Set-Content -Path (Join-Path $staging 'M-Dir.ini') -Value "[Start]`r`n" -Encoding Unicode
Set-Content -Path (Join-Path $staging 'Lang\README.txt') -Encoding UTF8 -Value @'
Language packs
==============
Korean and English are built in. To add a language, put a file named <code>.lng here (for example fr.lng)
and choose it in the Window > Language menu.

The file is UTF-8 text with one line per text: the Korean text, a TAB, the translation.
Use \n for a line break inside a text, and a line starting with # for a comment. Texts that are
missing fall back to English. Start from the English table: every Korean text of the program is
listed in src\MDir\Localization\EnglishStrings.cs.
'@

$zip = Join-Path $Output "M-Dir-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force

Write-Host "Program : $(Join-Path $publishDir 'M-Dir.exe')"
Write-Host "Portable: $zip"
