# L1 handler-mode DSL tests (ci-runner-dsl-spec.md §10, ci-runner-test-spec.md WH-010 area).

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestHelpers.ps1')
}

Describe 'Handler mode (§10)' {
    It 'Handler-001: return with no action exits 0' {
        $r = Invoke-TestHandler -HandlerContent 'return' -ExtraEnv @{ CI_HOOK_EVENT = 'push' }
        $r.ExitCode | Should -Be 0
    }

    It 'Handler-002: Get-HookPayload parses the saved payload JSON' {
        $fixture = New-CiTestFixture
        $payloadPath = Join-Path $fixture.WorkDir 'payload.json'
        Set-Content -Path $payloadPath -Value '{"ref":"refs/heads/main","after":"abc123"}' -Encoding utf8

        $r = Invoke-TestHandler -HandlerContent @'
$p = Get-HookPayload
if ($p.ref -ne 'refs/heads/main') { throw "unexpected ref: $($p.ref)" }
if ($p.after -ne 'abc123') { throw "unexpected after: $($p.after)" }
Write-Host 'ok'
'@ -ExtraEnv @{ CI_HOOK_PAYLOAD = $payloadPath }

        $r.ExitCode | Should -Be 0
        $r.Stdout | Should -Match 'ok'
    }

    It 'Handler-003: Get-HookHeader reads a value from the saved headers JSON' {
        $fixture = New-CiTestFixture
        $headersPath = Join-Path $fixture.WorkDir 'headers.json'
        Set-Content -Path $headersPath -Value '{"X-Custom":"hello","X-GitHub-Event":"push"}' -Encoding utf8

        $r = Invoke-TestHandler -HandlerContent @'
$v = Get-HookHeader 'X-Custom'
if ($v -ne 'hello') { throw "unexpected: $v" }
$missing = Get-HookHeader 'Does-Not-Exist'
if ($null -ne $missing) { throw "expected null for missing header" }
Write-Host 'ok'
'@ -ExtraEnv @{ CI_HOOK_HEADERS = $headersPath }

        $r.ExitCode | Should -Be 0
        $r.Stdout | Should -Match 'ok'
    }

    It 'Handler-004: Start-CiJob throws a clear error when CI_SERVER_URL is unset' {
        $r = Invoke-TestHandler -HandlerContent "Start-CiJob 'some-job'"
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'CI_SERVER_URL'
    }

    It 'Handler-005: Stage cannot be called from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'Stage "X" { Write-Host "no" }'
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'hook handler'
    }

    It 'Handler-006: PostStage cannot be called from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'PostStage "X" { Write-Host "no" }'
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'hook handler'
    }

    It 'Handler-007: Register-JUnit cannot be called from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'Register-JUnit "*.xml"'
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'hook handler'
    }

    It 'Handler-008: Register-Artifact cannot be called from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'Register-Artifact "*.hex"'
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'hook handler'
    }

    It 'Handler-009: Set-BuildNote cannot be called from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'Set-BuildNote "hi"'
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'hook handler'
    }

    It 'Handler-010: Exec is usable from a handler' {
        $r = Invoke-TestHandler -HandlerContent 'Exec { cmd /c "exit 0" }'
        $r.ExitCode | Should -Be 0
    }

    It 'Handler-011: no control file is required (CI_CONTROL_FILE unset)' {
        # A plain handler must not attempt to write control events at all.
        $r = Invoke-TestHandler -HandlerContent 'Write-Host "no control file needed"'
        $r.ExitCode | Should -Be 0
    }

    It 'Handler-012: throw in handler body exits 1 with the message on stderr' {
        $r = Invoke-TestHandler -HandlerContent "throw 'handler exploded'"
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'handler exploded'
    }

    It 'Handler-013: syntax error is caught before any handler code runs' {
        $r = Invoke-TestHandler -HandlerContent 'if ($true) { this is not valid ((('
        $r.ExitCode | Should -Be 1
        $r.Stderr | Should -Match 'handler\.cipipe'
    }
}
