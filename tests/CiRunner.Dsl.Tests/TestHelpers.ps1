# Shared fixture for L1 DSL module tests (ci-runner-test-spec.md §2).
# Runs bootstrap.ps1 as a real child process against a temp workspace/control-file setup,
# matching the actual runtime execution contract (ci-runner-dsl-spec.md §1-2) rather than
# in-process dot-sourcing, so module script-scope state never leaks between test cases.

$script:ModuleRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\src\CiRunner.Host\psmodule')
$script:BootstrapPath = Join-Path $script:ModuleRoot 'bootstrap.ps1'

function New-CiTestFixture {
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("cirunner-test-" + [guid]::NewGuid())
    $ws = Join-Path $work 'ws'
    $resultDir = Join-Path $work 'results'
    $artifactDir = Join-Path $work 'artifacts'
    New-Item -ItemType Directory -Path $ws, $resultDir, $artifactDir -Force | Out-Null
    $controlFile = Join-Path $work 'control.jsonl'
    New-Item -ItemType File -Path $controlFile -Force | Out-Null

    [PSCustomObject]@{
        WorkDir     = $work
        Workspace   = $ws
        ResultDir   = $resultDir
        ArtifactDir = $artifactDir
        ControlFile = $controlFile
    }
}

function Invoke-TestPipeline {
    param(
        [Parameter(Mandatory)][string]$PipelineContent,
        [string]$ShellPath = 'powershell.exe',
        [hashtable]$ExtraEnv = @{},
        [string]$BootstrapPath = $script:BootstrapPath
    )

    $fixture = New-CiTestFixture
    $pipelinePath = Join-Path $fixture.WorkDir 'pipeline.cipipe'
    Set-Content -Path $pipelinePath -Value $PipelineContent -Encoding utf8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ShellPath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $fixture.Workspace
    $psi.Arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$BootstrapPath`"", '-PipelinePath', "`"$pipelinePath`"") -join ' '
    $psi.Environment['CI_WORKSPACE'] = $fixture.Workspace
    $psi.Environment['CI_CONTROL_FILE'] = $fixture.ControlFile
    $psi.Environment['CI_RESULT_DIR'] = $fixture.ResultDir
    $psi.Environment['CI_ARTIFACT_DIR'] = $fixture.ArtifactDir
    foreach ($k in $ExtraEnv.Keys) {
        $psi.Environment[$k] = $ExtraEnv[$k]
    }

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    $events = @()
    if (Test-Path $fixture.ControlFile) {
        Get-Content -Path $fixture.ControlFile -Encoding UTF8 | Where-Object { $_.Trim() -ne '' } | ForEach-Object {
            $events += ($_ | ConvertFrom-Json)
        }
    }

    [PSCustomObject]@{
        ExitCode    = $p.ExitCode
        Stdout      = $stdout
        Stderr      = $stderr
        Events      = $events
        Fixture     = $fixture
    }
}

function Start-TestPipelineAsync {
    param(
        [Parameter(Mandatory)][string]$PipelineContent,
        [string]$ShellPath = 'powershell.exe'
    )

    $fixture = New-CiTestFixture
    $pipelinePath = Join-Path $fixture.WorkDir 'pipeline.cipipe'
    Set-Content -Path $pipelinePath -Value $PipelineContent -Encoding utf8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ShellPath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $fixture.Workspace
    $psi.Arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$script:BootstrapPath`"", '-PipelinePath', "`"$pipelinePath`"") -join ' '
    $psi.Environment['CI_WORKSPACE'] = $fixture.Workspace
    $psi.Environment['CI_CONTROL_FILE'] = $fixture.ControlFile
    $psi.Environment['CI_RESULT_DIR'] = $fixture.ResultDir
    $psi.Environment['CI_ARTIFACT_DIR'] = $fixture.ArtifactDir

    $p = [System.Diagnostics.Process]::Start($psi)
    [PSCustomObject]@{ Process = $p; Fixture = $fixture }
}

function Invoke-TestHandler {
    param(
        [Parameter(Mandatory)][string]$HandlerContent,
        [hashtable]$ExtraEnv = @{},
        [string]$ShellPath = 'powershell.exe'
    )

    $fixture = New-CiTestFixture
    $handlerPath = Join-Path $fixture.WorkDir 'handler.cipipe'
    Set-Content -Path $handlerPath -Value $HandlerContent -Encoding utf8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ShellPath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = $fixture.Workspace
    $psi.Arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$script:BootstrapPath`"", '-PipelinePath', "`"$handlerPath`"", '-Mode', 'Handler') -join ' '
    foreach ($k in $ExtraEnv.Keys) {
        $psi.Environment[$k] = $ExtraEnv[$k]
    }

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    [PSCustomObject]@{
        ExitCode = $p.ExitCode
        Stdout   = $stdout
        Stderr   = $stderr
        Fixture  = $fixture
    }
}

function Get-CiEvents {
    param([Parameter(Mandatory)][string]$ControlFile)
    if (-not (Test-Path $ControlFile)) {
        return @()
    }
    @(Get-Content -Path $ControlFile -Encoding UTF8 | Where-Object { $_.Trim() -ne '' } | ForEach-Object { $_ | ConvertFrom-Json })
}
