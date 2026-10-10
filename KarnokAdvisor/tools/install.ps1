param(
    [string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\The Bazaar',
    [string]$PythonExecutable = 'D:\miniconda\python.exe'
)
$ErrorActionPreference = 'Stop'
$advisorRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$resolvedGame = (Resolve-Path -LiteralPath $GameDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedGame 'TheBazaar.exe'))) { throw 'TheBazaar.exe not found.' }
if (Get-Process TheBazaar -ErrorAction SilentlyContinue) { throw 'Close The Bazaar before installing the addon.' }
if (-not (Test-Path -LiteralPath $PythonExecutable)) { throw 'Configured Python executable not found.' }
$pluginDirectory = Join-Path $resolvedGame 'BepInEx\plugins'
if (-not (Test-Path -LiteralPath $pluginDirectory -PathType Container)) { throw 'Install BepInEx/BazaarPlusPlus first.' }
$source = Join-Path $advisorRoot 'src\bin\Release\netstandard2.1\KarnokAdvisor.dll'
if (-not (Test-Path -LiteralPath $source)) { throw 'Run tools/build.ps1 first.' }
$destination = Join-Path $pluginDirectory 'KarnokAdvisor.dll'
Copy-Item -LiteralPath $source -Destination $destination -Force
$configuration = [ordered]@{
    runtime_dir = Join-Path $advisorRoot 'runtime'
    python_executable = (Resolve-Path -LiteralPath $PythonExecutable).Path
    advisor_script = Join-Path $advisorRoot 'advisor.py'
}
$settingsFile = Join-Path $pluginDirectory 'KarnokAdvisor.settings.json'
[IO.File]::WriteAllText($settingsFile, ($configuration | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'Installed DLL hash mismatch.' }
Write-Output "Installed independent addon: $destination"
Write-Output "Settings: $settingsFile"
Write-Output 'Existing BazaarPlusPlus files were not changed. Start the game; F9 toggles the advisor panel.'
