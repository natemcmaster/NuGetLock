#!/usr/bin/env powershell

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function __exec($_cmd) {
    write-host -ForegroundColor Cyan ">>> $_cmd $args"
    $ErrorActionPreference = 'Continue'
    & $_cmd @args
    $exit_code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exit_code -ne 0) {
        write-error "Failed with exit code $exit_code"
        exit 1
    }
}

__exec dotnet restore
__exec dotnet build -c Release
$artifacts = Join-Path $PSScriptRoot artifacts
New-Item -ItemType Directory -Path $artifacts -ErrorAction Ignore | Out-Null
__exec dotnet pack -c Release ./src/NuGetLock/NuGetLock.csproj -o $artifacts
