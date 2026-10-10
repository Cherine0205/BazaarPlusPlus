param([string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\The Bazaar')
$ErrorActionPreference = 'Stop'
$advisorRoot = Split-Path -Parent $PSScriptRoot
$workspaceDirectory = Split-Path -Parent $advisorRoot
$env:DOTNET_CLI_HOME = Join-Path $workspaceDirectory '.cache\dotnet'
$env:NUGET_PACKAGES = Join-Path $workspaceDirectory '.cache\nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $workspaceDirectory '.cache\nuget-http'
$env:NUGET_SCRATCH = Join-Path $workspaceDirectory '.cache\nuget-scratch'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $workspaceDirectory '.cache\nuget-plugins'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet build (Join-Path $advisorRoot 'src\KarnokAdvisor.csproj') -c Release --nologo -p:NuGetAudit=false "-p:GameDirectory=$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw 'KarnokAdvisor build failed.' }
