param(
    [string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\The Bazaar',
    [string]$PythonExecutable = 'python'
)
$ErrorActionPreference = 'Stop'
$advisorRoot = Split-Path -Parent $PSScriptRoot
& $PythonExecutable -m unittest discover -s (Join-Path $advisorRoot 'tests') -v
if ($LASTEXITCODE -ne 0) { throw 'Handbook Python checks failed.' }
& (Join-Path $PSScriptRoot 'build.ps1') -GameDirectory $GameDirectory
dotnet run --project (Join-Path $advisorRoot 'tests\ReferenceView.Tests\ReferenceView.Tests.csproj') -c Release -p:NuGetAudit=false "-p:GameDirectory=$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Handbook view checks failed.' }
