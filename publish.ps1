<#
.SYNOPSIS
  Builds the release packages of M-Dir.

.DESCRIPTION
  Two variants of the same program, each as a plain folder and as a portable zip:

    artifacts\M-Dir\M-Dir.exe       self-contained, about 49 MB, runs on any Windows 10/11 x64 without .NET
    artifacts\M-Dir-lite\M-Dir.exe  framework-dependent, under 1 MB, needs the .NET 10 Desktop Runtime
                                    (https://dotnet.microsoft.com/download/dotnet/10.0, "Desktop Runtime")

    artifacts\M-Dir-<version>-<rid>.zip       portable zip of the self-contained one
    artifacts\M-Dir-<version>-<rid>-lite.zip  portable zip of the small one

  A portable zip holds M-Dir.exe, an empty M-Dir.ini (its presence next to the exe makes M-Dir keep settings,
  favorites and workspaces beside the exe and leave the registry and %APPDATA% alone) and Lang\README.txt.

  The size cannot be reduced further by trimming: the .NET SDK does not support trimming Windows Forms (NETSDK1175).

  Run from the repository root:  .\publish.ps1   (or  .\publish.ps1 -Runtime win-arm64)
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot 'artifacts')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\MDir\MDir.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Publish-Variant([string]$Name, [bool]$SelfContained, [string]$ZipSuffix) {
    $publishDir = Join-Path $Output $Name
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish $project -c Release -r $Runtime "-p:SelfContained=$($SelfContained.ToString().ToLower())" -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($Name) failed ($LASTEXITCODE)" }

    # Portable package: a staging folder so the plain exe above stays free of the marker file.
    $staging = Join-Path $Output "portable-$Name"
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

    $zip = Join-Path $Output "M-Dir-$version-$Runtime$ZipSuffix.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item $staging -Recurse -Force

    $exe = Get-Item (Join-Path $publishDir 'M-Dir.exe')
    Write-Host ("{0,-10} {1,8:N1} MB  {2}" -f $Name, ($exe.Length / 1MB), $exe.FullName)
    Write-Host ("{0,-10} {1,8:N1} MB  {2}" -f 'zip', ((Get-Item $zip).Length / 1MB), $zip)
}

Publish-Variant -Name 'M-Dir' -SelfContained $true -ZipSuffix ''
Publish-Variant -Name 'M-Dir-lite' -SelfContained $false -ZipSuffix '-lite'
