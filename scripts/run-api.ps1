$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
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
Load-HostConnection 'DATABASE_URL'
Load-HostConnection 'ADMIN_DATABASE_URL'
$ollama = [Environment]::GetEnvironmentVariable('OLLAMA_BASE_URL', 'Process')
if ($null -eq $ollama) {
    $envFile = Join-Path $root '.env'
    if (Test-Path -LiteralPath $envFile) { foreach ($line in Get-Content -LiteralPath $envFile) { if ($line -match '^\s*OLLAMA_BASE_URL\s*=\s*(.*)$') { $ollama = $Matches[1].Trim().Trim('"').Trim("'"); break } } }
}
if ($null -eq [Environment]::GetEnvironmentVariable('OLLAMA_BASE_URL','Process') -and $null -ne $ollama) {
    $ollama = $ollama -replace '://host\.docker\.internal(?=[:/])', '://localhost'
    [Environment]::SetEnvironmentVariable('OLLAMA_BASE_URL', $ollama, 'Process')
}
dotnet run --project (Join-Path $root 'src/DomainCopilot.Api/DomainCopilot.Api.csproj') -- @args
exit $LASTEXITCODE
