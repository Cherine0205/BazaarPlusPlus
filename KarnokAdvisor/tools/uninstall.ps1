param([string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\The Bazaar')
$ErrorActionPreference = 'Stop'
if (Get-Process TheBazaar -ErrorAction SilentlyContinue) { throw 'Close The Bazaar before removing the addon.' }
$resolvedGame = (Resolve-Path -LiteralPath $GameDirectory).Path
$plugins = [IO.Path]::GetFullPath((Join-Path $resolvedGame 'BepInEx\plugins'))
foreach ($name in @('KarnokAdvisor.dll', 'KarnokAdvisor.settings.json')) {
    $target = [IO.Path]::GetFullPath((Join-Path $plugins $name))
    if ([IO.Path]::GetDirectoryName($target) -ne $plugins) { throw 'Unexpected target directory.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
}
Write-Output 'KarnokAdvisor addon removed. Recommendation logs and existing mods were retained.'
