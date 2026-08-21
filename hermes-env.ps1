# Load .env from the repo root into the current PowerShell session,
# then launch the Hermes CLI.
# Usage:  .\hermes-env.ps1            (loads vars only)
#         .\hermes-env.ps1 chat       (loads vars + runs CLI)

$envFile = Join-Path $PSScriptRoot ".env"

if (-not (Test-Path $envFile)) {
    Write-Host "No .env found at $envFile" -ForegroundColor Red
    exit 1
}

Get-Content $envFile | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object {
    $k, $v = $_ -split '=', 2
    if ($k) {
        [Environment]::SetEnvironmentVariable($k.Trim(), $v.Trim(), 'Process')
    }
}

Write-Host "Loaded .env from $envFile" -ForegroundColor Green

if ($args.Count -gt 0) {
    dotnet run --project (Join-Path $PSScriptRoot "src\HermesAgent.Cli") @args
}
