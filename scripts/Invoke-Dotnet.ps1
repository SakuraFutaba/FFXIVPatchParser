#requires -Version 5.1
# Run with: ./scripts/Invoke-Dotnet.ps1 build src/FFXIVPatchParser
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$settings = @{
    TEMP='tmp/temp'; TMP='tmp/temp'; DOTNET_CLI_HOME='tmp/dotnet-home'
    NUGET_PACKAGES='tmp/nuget/packages'; NUGET_HTTP_CACHE_PATH='tmp/nuget/http'
    NUGET_PLUGINS_CACHE_PATH='tmp/nuget/plugins'; MSBUILDDEBUGPATH='tmp/msbuild'
}
$original = @{}
try {
    foreach ($key in $settings.Keys) {
        $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        $path = Join-Path $projectRoot $settings[$key]
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        [Environment]::SetEnvironmentVariable($key, $path, 'Process')
    }
    foreach ($key in @('DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE')) {
        $original[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, '1', 'Process')
    }
    $original['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = [Environment]::GetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE', 'false', 'Process')
    Push-Location $projectRoot
    try { & dotnet @args; $code = $LASTEXITCODE } finally { Pop-Location }
} finally {
    foreach ($key in $original.Keys) { [Environment]::SetEnvironmentVariable($key, $original[$key], 'Process') }
}
exit $code
