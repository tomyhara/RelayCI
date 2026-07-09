param([Parameter(Mandatory)][string]$PipelinePath)
$ErrorActionPreference = 'Stop'

try {
    Import-Module "$PSScriptRoot\CiRunner.psm1" -Force
    Initialize-CiRunner
}
catch {
    [Console]::Error.WriteLine("bootstrap: $($_.Exception.Message)")
    exit 2
}

# .cipipe is not a .ps1 and cannot be dot-sourced directly.
# Parse it into a ScriptBlock so syntax errors are caught before any stage runs.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $PipelinePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { [Console]::Error.WriteLine($_.ToString()) }
    Write-CiEvent 'error' @{ message = "syntax error: $($parseErrors[0])" }
    exit 1
}

$failed = $false
try {
    Set-Location $env:CI_WORKSPACE
    . $ast.GetScriptBlock()
}
catch {
    $failed = $true
    if (-not (Test-CiStageHandled $_)) {
        Write-CiEvent 'error' @{ message = $_.Exception.Message }
    }
}
finally {
    if (-not (Invoke-CiPostStages -BuildFailed:$failed)) {
        $failed = $true
    }
}

if ($failed) { exit 1 } else { exit 0 }
