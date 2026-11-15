$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$environmentNames = @('DATABASE_URL', 'ADMIN_DATABASE_URL', 'OLLAMA_BASE_URL', 'API_BASE_URL')
$originalEnvironment = @{}
foreach ($name in $environmentNames) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Load-HostConnection([string]$name) {
    if ($null -ne [Environment]::GetEnvironmentVariable($name, 'Process')) { return }
    $file = Join-Path $root '.env'
    if (-not (Test-Path -LiteralPath $file)) { return }
    foreach ($line in Get-Content -LiteralPath $file) {
        if ($line -match "^\s*$name\s*=\s*(.*)$") {
            $value = $Matches[1].Trim()
            if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) { $value = $value.Substring(1, $value.Length - 2) }
            $value = $value -replace '(?<=@)db(?=:\d+)', 'localhost' -replace '(?<=@)host\.docker\.internal(?=:\d+)', 'localhost'
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
            return
        }
    }
}
$exitCode = 1
try {
    Load-HostConnection 'DATABASE_URL'
    Load-HostConnection 'ADMIN_DATABASE_URL'
    $ollama = [Environment]::GetEnvironmentVariable('OLLAMA_BASE_URL', 'Process')
    if ($null -eq $ollama) {
        $envFile = Join-Path $root '.env'
        if (Test-Path -LiteralPath $envFile) { foreach ($line in Get-Content -LiteralPath $envFile) { if ($line -match '^\s*OLLAMA_BASE_URL\s*=\s*(.*)$') { $ollama = $Matches[1].Trim().Trim('"').Trim("'"); break } } }
    }
    if ($null -eq [Environment]::GetEnvironmentVariable('OLLAMA_BASE_URL', 'Process') -and $null -ne $ollama) {
        $ollama = $ollama -replace '://host\.docker\.internal(?=[:/])', '://localhost'
        [Environment]::SetEnvironmentVariable('OLLAMA_BASE_URL', $ollama, 'Process')
    }
    if ($null -eq [Environment]::GetEnvironmentVariable('API_BASE_URL', 'Process')) {
        [Environment]::SetEnvironmentVariable('API_BASE_URL', 'http://localhost:18080/', 'Process')
    }
    dotnet run --project (Join-Path $root 'src/DomainCopilot.Cli/DomainCopilot.Cli.csproj') -- @args
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}
exit $exitCode