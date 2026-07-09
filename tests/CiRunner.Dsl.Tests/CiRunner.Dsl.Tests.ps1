# L1 DSL module tests (ci-runner-test-spec.md §2). Test IDs in `It` names map to that table.
#
# Note: Pester enables `Set-StrictMode -Version Latest`, under which `.Count` on a single
# (non-array) PSCustomObject throws PropertyNotFoundException instead of returning 1. Every
# Where-Object result that flows into `.Count` or index access below is therefore wrapped in
# `@(...)` to force array context.

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
}

Describe 'Stage (§2.1)' {
    It 'DSL-001: two successful stages record seq 1,2 and exit 0' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Write-Host "a" }
Stage "Two" { Write-Host "b" }
'@
        $r.ExitCode | Should -Be 0
        $starts = @($r.Events | Where-Object ev -eq 'stage-start')
        $ends = @($r.Events | Where-Object ev -eq 'stage-end')
        $starts.Count | Should -Be 2
        $ends.Count | Should -Be 2
        $starts[0].seq | Should -Be 1
        $starts[1].seq | Should -Be 2
        @($ends | Where-Object { $_.status -ne 'success' }).Count | Should -Be 0
        @($r.Stdout -split "`n" | Where-Object { $_ -match '\[Stage\]' }).Count | Should -Be 2
    }

    It 'DSL-002: throw in second stage fails build, third stage never starts' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Write-Host "a" }
Stage "Two" { throw "boom" }
Stage "Three" { Write-Host "c" }
'@
        $r.ExitCode | Should -Be 1
        $ends = @($r.Events | Where-Object ev -eq 'stage-end')
        ($ends | Where-Object name -eq 'Two').status | Should -Be 'failed'
        ($ends | Where-Object name -eq 'Two').error | Should -Match 'boom'
        @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.name -eq 'Three' }).Count | Should -Be 0
    }

    It 'DSL-003: error message over 4KB is truncated to 4096 chars' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { throw ("x" * 5000) }
'@
        $r.ExitCode | Should -Be 1
        $end = $r.Events | Where-Object ev -eq 'stage-end'
        $end.error.Length | Should -Be 4096
    }

    It 'DSL-004: nested Stage throws' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "Outer" { Stage "Inner" { Write-Host "never" } }
'@
        $r.ExitCode | Should -Be 1
        @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.name -eq 'Inner' }).Count | Should -Be 0
        $outerEnd = $r.Events | Where-Object { $_.ev -eq 'stage-end' -and $_.name -eq 'Outer' }
        $outerEnd.status | Should -Be 'failed'
        $outerEnd.error | Should -Match 'nested'
    }

    It 'DSL-005: duplicate stage name throws' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "X" { Write-Host "1" }
Stage "X" { Write-Host "2" }
'@
        $r.ExitCode | Should -Be 1
        @($r.Events | Where-Object ev -eq 'stage-start').Count | Should -Be 1
    }

    It 'DSL-006: LASTEXITCODE resets to 0 at next stage start' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Exec { cmd /c "exit 1" } -AllowExitCodes 0,1 }
Stage "Two" { if ($LASTEXITCODE -ne 0) { throw "not reset: $LASTEXITCODE" } }
'@
        $r.ExitCode | Should -Be 0
    }
}

Describe 'PostStage (§2.2)' {
    It 'DSL-010: success build runs Always+Success, skips Failure' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "A" { Write-Host "always" }
PostStage "S" -When Success { Write-Host "success" }
PostStage "F" -When Failure { Write-Host "failure" }
Stage "One" { Write-Host "ok" }
'@
        $r.ExitCode | Should -Be 0
        $postStarts = @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.post -ne $null })
        $postStarts.Count | Should -Be 2
        ($postStarts | Where-Object name -eq 'A').post | Should -Be 'always'
        ($postStarts | Where-Object name -eq 'S').post | Should -Be 'success'
        @($postStarts | Where-Object name -eq 'F').Count | Should -Be 0
    }

    It 'DSL-011: failed build runs Always+Failure, skips Success' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "A" { Write-Host "always" }
PostStage "S" -When Success { Write-Host "success" }
PostStage "F" -When Failure { Write-Host "failure" }
Stage "One" { throw "boom" }
'@
        $r.ExitCode | Should -Be 1
        $postStarts = @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.post -ne $null })
        $postStarts.Count | Should -Be 2
        @($postStarts | Where-Object name -eq 'S').Count | Should -Be 0
        @($postStarts | Where-Object name -eq 'F').Count | Should -Be 1
    }

    It 'DSL-012: mixed When runs in registration order, skipping non-matching' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "S1" -When Success { Write-Host "s1" }
PostStage "A1" { Write-Host "a1" }
PostStage "S2" -When Success { Write-Host "s2" }
Stage "One" { Write-Host "ok" }
'@
        $r.ExitCode | Should -Be 0
        $postStarts = @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.post -ne $null })
        @($postStarts | ForEach-Object name) | Should -Be @('S1', 'A1', 'S2')
    }

    It 'DSL-013: failure inside PostStage still runs remaining matching PostStages' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "P1" { throw "post fail" }
PostStage "P2" { Write-Host "p2 ran" }
Stage "One" { Write-Host "ok" }
'@
        $r.ExitCode | Should -Be 1
        $ends = @($r.Events | Where-Object { $_.ev -eq 'stage-end' -and $_.name -in @('P1', 'P2') })
        ($ends | Where-Object name -eq 'P1').status | Should -Be 'failed'
        ($ends | Where-Object name -eq 'P2').status | Should -Be 'success'
    }

    It 'DSL-014: Success post failing does not trigger skipped Failure post (no re-evaluation)' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "S" -When Success { throw "success post fails" }
PostStage "F" -When Failure { Write-Host "should not run" }
Stage "One" { Write-Host "ok" }
'@
        $r.ExitCode | Should -Be 1
        @($r.Events | Where-Object { $_.ev -eq 'stage-start' -and $_.name -eq 'F' }).Count | Should -Be 0
    }

    It 'DSL-015: Stage call from within PostStage throws' {
        $r = Invoke-TestPipeline -PipelineContent @'
PostStage "P" { Stage "Illegal" { Write-Host "no" } }
Stage "One" { Write-Host "ok" }
'@
        $r.ExitCode | Should -Be 1
        $pEnd = $r.Events | Where-Object { $_.ev -eq 'stage-end' -and $_.name -eq 'P' }
        $pEnd.status | Should -Be 'failed'
    }

    It 'DSL-016: PostStage written after a failing Stage is never registered or executed' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { throw "boom" }
PostStage "TooLate" { Write-Host "never" }
'@
        $r.ExitCode | Should -Be 1
        @($r.Events | Where-Object { $_.name -eq 'TooLate' }).Count | Should -Be 0
    }
}

Describe 'Exec / Register / misc (§2.3)' {
    It 'DSL-020: Exec with exit 0 does not throw' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Exec { cmd /c "exit 0" } }
'@
        $r.ExitCode | Should -Be 0
    }

    It 'DSL-021: Exec with exit 3 throws mentioning code 3' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Exec { cmd /c "exit 3" } }
'@
        $r.ExitCode | Should -Be 1
        $end = $r.Events | Where-Object ev -eq 'stage-end'
        $end.error | Should -Match 'code 3'
    }

    It 'DSL-022: -AllowExitCodes 0,1,2,3 permits exit 3' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Exec { cmd /c "exit 3" } -AllowExitCodes 0,1,2,3 }
'@
        $r.ExitCode | Should -Be 0
    }

    It 'DSL-023: cmdlet error without Exec still fails the stage' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Get-Item "Z:\definitely\does\not\exist\at\all" }
'@
        $r.ExitCode | Should -Be 1
    }

    It 'DSL-030: Register-JUnit copies matched files with numeric prefix' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    New-Item -ItemType Directory -Path results -Force | Out-Null
    "<a/>" | Set-Content results\r1.xml
    "<b/>" | Set-Content results\r2.xml
    "<c/>" | Set-Content results\r3.xml
    Register-JUnit "results\*.xml"
}
'@
        $r.ExitCode | Should -Be 0
        $junit = $r.Events | Where-Object ev -eq 'junit'
        $junit.files.Count | Should -Be 3
        @(Get-ChildItem $r.Fixture.ResultDir).Count | Should -Be 3
    }

    It 'DSL-031: duplicate filenames in different directories both copied without collision' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    New-Item -ItemType Directory -Path results\a, results\b -Force | Out-Null
    "<a/>" | Set-Content results\a\same.xml
    "<b/>" | Set-Content results\b\same.xml
    Register-JUnit "results\**\*.xml"
}
'@
        $r.ExitCode | Should -Be 0
        @(Get-ChildItem $r.Fixture.ResultDir).Count | Should -Be 2
    }

    It 'DSL-032: zero matches (default) warns but stage succeeds' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Register-JUnit "nomatch\*.xml" }
'@
        $r.ExitCode | Should -Be 0
        @($r.Events | Where-Object ev -eq 'warning').Count | Should -Be 1
    }

    It 'DSL-033: zero matches with -Required throws' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Register-JUnit "nomatch\*.xml" -Required }
'@
        $r.ExitCode | Should -Be 1
    }

    It 'DSL-040: Register-Artifact preserves relative directory structure' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    New-Item -ItemType Directory -Path out\map -Force | Out-Null
    "hex" | Set-Content out\fw.hex
    "map" | Set-Content out\map\fw.map
    Register-Artifact "out\**\*.*"
}
'@
        $r.ExitCode | Should -Be 0
        $artifact = $r.Events | Where-Object ev -eq 'artifact'
        $artifact.files.Count | Should -Be 2
        Test-Path (Join-Path $r.Fixture.ArtifactDir 'out\fw.hex') | Should -Be $true
        Test-Path (Join-Path $r.Fixture.ArtifactDir 'out\map\fw.map') | Should -Be $true
    }

    It 'DSL-041: -Flatten with name collision gets a numeric suffix' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    New-Item -ItemType Directory -Path out\a, out\b -Force | Out-Null
    "1" | Set-Content out\a\same.txt
    "2" | Set-Content out\b\same.txt
    Register-Artifact "out\**\*.txt" -Flatten
}
'@
        $r.ExitCode | Should -Be 0
        @(Get-ChildItem $r.Fixture.ArtifactDir -File).Count | Should -Be 2
    }

    It 'DSL-050: Set-BuildNote truncates to 200 chars' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Set-BuildNote ("y" * 250) }
'@
        $r.ExitCode | Should -Be 0
        $note = $r.Events | Where-Object ev -eq 'note'
        $note.text.Length | Should -Be 200
    }

    It 'DSL-051: Set-BuildNote called twice records two events' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Set-BuildNote "first" }
Stage "Two" { Set-BuildNote "second" }
'@
        $r.ExitCode | Should -Be 0
        @($r.Events | Where-Object ev -eq 'note').Count | Should -Be 2
    }

    It 'DSL-052: Set-CiEnv propagates to child processes and emits no event' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    Set-CiEnv MY_TEST_VAR "hello123"
    Exec { cmd /c "echo %MY_TEST_VAR%" }
}
'@
        $r.ExitCode | Should -Be 0
        $r.Stdout | Should -Match 'hello123'
        @($r.Events | Where-Object { $_.ev -eq 'setenv' }).Count | Should -Be 0
    }
}

Describe 'bootstrap / control file (§2.4)' {
    It 'DSL-060: syntax error reports file/line and produces zero stage events' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" {
    this is not valid { powershell (((
'@
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'pipeline\.cipipe'
        @($r.Events | Where-Object ev -eq 'error').Count | Should -Be 1
        @($r.Events | Where-Object ev -eq 'stage-start').Count | Should -Be 0
    }

    It 'DSL-061: module import failure exits 2' {
        $isolatedDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cirunner-brokenmod-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $isolatedDir -Force | Out-Null
        Copy-Item $script:BootstrapPath (Join-Path $isolatedDir 'bootstrap.ps1')
        # Deliberately do not copy CiRunner.psm1: Import-Module must fail.
        $r = Invoke-TestPipeline -PipelineContent 'Stage "One" { Write-Host "unreachable" }' -BootstrapPath (Join-Path $isolatedDir 'bootstrap.ps1')
        $r.ExitCode | Should -Be 2
    }

    It 'DSL-062: throw outside any Stage records an error event, not stage-end' {
        $r = Invoke-TestPipeline -PipelineContent @'
throw "outside stage"
'@
        $r.ExitCode | Should -Be 1
        @($r.Events | Where-Object ev -eq 'error').Count | Should -Be 1
        @($r.Events | Where-Object ev -eq 'stage-end').Count | Should -Be 0
    }

    It 'DSL-063: control file is valid JSON Lines, UTF-8 no BOM, starts with start(v=1)' {
        $r = Invoke-TestPipeline -PipelineContent @'
Stage "One" { Write-Host "ok" }
'@
        $bytes = [System.IO.File]::ReadAllBytes($r.Fixture.ControlFile)
        # UTF-8 BOM would be EF BB BF
        ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) | Should -Be $false
        $r.Events[0].ev | Should -Be 'start'
        $r.Events[0].v | Should -Be 1
        $r.Events[0].pid | Should -BeGreaterThan 0
        $r.Events[0].psVersion | Should -Not -BeNullOrEmpty
    }

    It 'DSL-064: stage-start is observable on disk while the stage is still running' {
        $started = Start-TestPipelineAsync -PipelineContent @'
Stage "Slow" {
    Start-Sleep -Seconds 3
}
'@
        try {
            $seenMidRun = $false
            for ($i = 0; $i -lt 50; $i++) {
                Start-Sleep -Milliseconds 100
                $events = Get-CiEvents -ControlFile $started.Fixture.ControlFile
                if (@($events | Where-Object ev -eq 'stage-start').Count -gt 0 -and -not $started.Process.HasExited) {
                    $seenMidRun = $true
                    break
                }
            }
            $seenMidRun | Should -Be $true
        }
        finally {
            if (-not $started.Process.HasExited) {
                $started.Process.WaitForExit(10000) | Out-Null
            }
        }
    }
}

Describe 'PS version matrix (§2.4 DSL-070)' {
    $pwshAvailable = [bool](Get-Command pwsh -ErrorAction SilentlyContinue)

    It 'DSL-070: same pipeline passes under pwsh 7 as under Windows PowerShell 5.1' -Skip:(-not $pwshAvailable) {
        $r = Invoke-TestPipeline -ShellPath 'pwsh' -PipelineContent @'
Stage "One" { Write-Host "a" }
Stage "Two" { Write-Host "b" }
'@
        $r.ExitCode | Should -Be 0
        $r.Events[0].psVersion | Should -Match '^7\.'
    }
}
