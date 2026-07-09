#
# CiRunner.psm1 - Pipeline DSL module. See ci-runner-dsl-spec.md for the authoritative contract.
#
$ErrorActionPreference = 'Stop'

$script:ControlWriter = $null
$script:StageSeq = 0
$script:StageNames = @{}
$script:PostStages = @()
$script:PostPhase = $false
$script:InStage = $false
$script:InPostStage = $false
$script:HandlerMode = $false

function Write-CiEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$EventType,
        [Parameter(Position = 1)][hashtable]$Payload = @{}
    )
    if (-not $script:ControlWriter) {
        $path = $env:CI_CONTROL_FILE
        if (-not $path) {
            throw 'CI_CONTROL_FILE is not set'
        }
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        $encoding = New-Object System.Text.UTF8Encoding($false)
        $script:ControlWriter = New-Object System.IO.StreamWriter($fs, $encoding)
        $script:ControlWriter.AutoFlush = $true
    }

    $obj = [ordered]@{
        t  = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffK')
        ev = $EventType
    }
    foreach ($key in $Payload.Keys) {
        $obj[$key] = $Payload[$key]
    }
    $json = $obj | ConvertTo-Json -Compress -Depth 8
    $script:ControlWriter.WriteLine($json)
}

function Initialize-CiRunner {
    [CmdletBinding()]
    param()
    $script:StageSeq = 0
    $script:StageNames = @{}
    $script:PostStages = @()
    $script:PostPhase = $false
    $script:InStage = $false
    $script:InPostStage = $false
    $script:HandlerMode = $false
    Write-CiEvent -EventType 'start' -Payload @{
        v         = 1
        pid       = $PID
        psVersion = $PSVersionTable.PSVersion.ToString()
    }
}

function Initialize-CiHandler {
    [CmdletBinding()]
    param()
    # Handlers have no control file / stage lifecycle (DSL spec §10.1) - only Get-HookPayload,
    # Get-HookHeader, Start-CiJob and Exec are meaningful here.
    $script:HandlerMode = $true
}

function Test-CiStageHandled {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)]$ErrorRecord)
    return $ErrorRecord.Exception.Data.Contains('CiStageHandled')
}

function Stage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [Parameter(Mandatory, Position = 1)][scriptblock]$Body
    )
    if ($script:HandlerMode) {
        throw "Stage '$Name' cannot be called from a hook handler"
    }
    if ($script:PostPhase) {
        throw "Stage '$Name' called after PostStage execution phase has begun"
    }
    if ($script:InPostStage) {
        throw "Stage '$Name' cannot be called from within a PostStage"
    }
    if ($script:InStage) {
        throw "Stage '$Name' cannot be nested inside another Stage"
    }
    if ($script:StageNames.ContainsKey($Name)) {
        throw "Duplicate stage name: '$Name'"
    }
    $script:StageNames[$Name] = $true

    $script:StageSeq++
    $seq = $script:StageSeq
    Write-CiEvent -EventType 'stage-start' -Payload @{ seq = $seq; name = $Name; post = $null }
    Write-Host "========== [Stage] $Name =========="
    $global:LASTEXITCODE = 0

    $script:InStage = $true
    try {
        & $Body
        Write-CiEvent -EventType 'stage-end' -Payload @{ seq = $seq; name = $Name; status = 'success' }
    }
    catch {
        $message = $_.Exception.Message
        if ($message.Length -gt 4096) {
            $message = $message.Substring(0, 4096)
        }
        Write-CiEvent -EventType 'stage-end' -Payload @{ seq = $seq; name = $Name; status = 'failed'; error = $message }
        $_.Exception.Data['CiStageHandled'] = $true
        throw
    }
    finally {
        $script:InStage = $false
    }
}

function PostStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [ValidateSet('Success', 'Failure', 'Always')][string]$When = 'Always',
        [Parameter(Mandatory, Position = 1)][scriptblock]$Body
    )
    if ($script:HandlerMode) {
        throw "PostStage '$Name' cannot be called from a hook handler"
    }
    if ($script:InStage) {
        throw "PostStage '$Name' cannot be called from within a Stage"
    }
    if ($script:InPostStage) {
        throw "PostStage '$Name' cannot be nested inside another PostStage"
    }
    if ($script:PostPhase) {
        throw "PostStage '$Name' registered after PostStage execution phase has begun"
    }

    $script:PostStages += [PSCustomObject]@{ Name = $Name; When = $When; Body = $Body }
}

function Invoke-CiPostStages {
    [CmdletBinding()]
    param([Parameter(Mandatory)][bool]$BuildFailed)

    $script:PostPhase = $true
    $resultWhen = if ($BuildFailed) { 'Failure' } else { 'Success' }
    $anyPostFailed = $false

    foreach ($post in $script:PostStages) {
        if ($post.When -ne 'Always' -and $post.When -ne $resultWhen) {
            continue
        }

        $script:StageSeq++
        $seq = $script:StageSeq
        $postField = $post.When.ToLowerInvariant()
        Write-CiEvent -EventType 'stage-start' -Payload @{ seq = $seq; name = $post.Name; post = $postField }
        Write-Host "========== [Stage] $($post.Name) =========="
        $global:LASTEXITCODE = 0

        $script:InPostStage = $true
        try {
            & $post.Body
            Write-CiEvent -EventType 'stage-end' -Payload @{ seq = $seq; name = $post.Name; status = 'success' }
        }
        catch {
            $message = $_.Exception.Message
            if ($message.Length -gt 4096) {
                $message = $message.Substring(0, 4096)
            }
            Write-CiEvent -EventType 'stage-end' -Payload @{ seq = $seq; name = $post.Name; status = 'failed'; error = $message }
            $anyPostFailed = $true
        }
        finally {
            $script:InPostStage = $false
        }
    }

    return -not $anyPostFailed
}

function Exec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][scriptblock]$Body,
        [int[]]$AllowExitCodes = @(0)
    )
    & $Body
    if ($AllowExitCodes -notcontains $LASTEXITCODE) {
        throw "Command exited with code $LASTEXITCODE"
    }
}

function Resolve-CiGlobPattern {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string]$Pattern)

    $normalized = $Pattern -replace '/', '\'
    if ($normalized -notmatch '\*\*') {
        return @(Get-ChildItem -Path $normalized -File -ErrorAction SilentlyContinue)
    }

    $starIndex = $normalized.IndexOf('**')
    $prefix = $normalized.Substring(0, $starIndex).TrimEnd('\')
    $suffix = $normalized.Substring($starIndex + 2).TrimStart('\')
    if ([string]::IsNullOrEmpty($prefix)) {
        $prefix = '.'
    }
    if ([string]::IsNullOrEmpty($suffix)) {
        $suffix = '*'
    }
    if (-not (Test-Path -Path $prefix)) {
        return @()
    }

    $leafPattern = Split-Path -Path $suffix -Leaf
    return @(Get-ChildItem -Path $prefix -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like $leafPattern })
}

function Register-JUnit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Glob,
        [switch]$Required
    )
    if ($script:HandlerMode) {
        throw 'Register-JUnit cannot be called from a hook handler'
    }
    $files = Resolve-CiGlobPattern -Pattern $Glob
    if (-not $files -or $files.Count -eq 0) {
        $msg = "Register-JUnit: no files matched '$Glob'"
        if ($Required) {
            throw $msg
        }
        Write-Host "WARNING: $msg"
        Write-CiEvent -EventType 'warning' -Payload @{ message = $msg }
        return
    }

    $resultDir = $env:CI_RESULT_DIR
    $copied = @()
    $i = 0
    foreach ($f in $files) {
        $i++
        $destName = '{0:D3}_{1}' -f $i, $f.Name
        Copy-Item -Path $f.FullName -Destination (Join-Path $resultDir $destName) -Force
        $copied += $destName
    }
    Write-CiEvent -EventType 'junit' -Payload @{ files = $copied }
}

function Register-Artifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Glob,
        [switch]$Required,
        [switch]$Flatten
    )
    if ($script:HandlerMode) {
        throw 'Register-Artifact cannot be called from a hook handler'
    }
    $files = Resolve-CiGlobPattern -Pattern $Glob
    if (-not $files -or $files.Count -eq 0) {
        $msg = "Register-Artifact: no files matched '$Glob'"
        if ($Required) {
            throw $msg
        }
        Write-Host "WARNING: $msg"
        Write-CiEvent -EventType 'warning' -Payload @{ message = $msg }
        return
    }

    $artifactDir = $env:CI_ARTIFACT_DIR
    $workspaceRoot = (Get-Location).Path
    $usedNames = @{}
    $entries = @()

    foreach ($f in $files) {
        if ($Flatten) {
            $destRel = $f.Name
            if ($usedNames.ContainsKey($destRel)) {
                $usedNames[$destRel]++
                $destRel = '{0}_{1}{2}' -f [System.IO.Path]::GetFileNameWithoutExtension($f.Name), $usedNames[$f.Name], [System.IO.Path]::GetExtension($f.Name)
            }
            else {
                $usedNames[$destRel] = 0
            }
        }
        else {
            $rel = $f.FullName
            if ($rel.StartsWith($workspaceRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                $rel = $rel.Substring($workspaceRoot.Length).TrimStart('\', '/')
            }
            else {
                $rel = $f.Name
            }
            $destRel = $rel
        }

        $destPath = Join-Path $artifactDir $destRel
        $destDir = Split-Path -Path $destPath -Parent
        if ($destDir -and -not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item -Path $f.FullName -Destination $destPath -Force
        $entries += @{ path = ($destRel -replace '\\', '/'); size = $f.Length }
    }

    Write-CiEvent -EventType 'artifact' -Payload @{ files = $entries }
}

function Set-CiEnv {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [Parameter(Mandatory, Position = 1)][string]$Value
    )
    Set-Item -Path "Env:$Name" -Value $Value
}

function Set-BuildNote {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string]$Text)
    if ($script:HandlerMode) {
        throw 'Set-BuildNote cannot be called from a hook handler'
    }
    $t = $Text
    if ($t.Length -gt 200) {
        $t = $t.Substring(0, 200)
    }
    Write-CiEvent -EventType 'note' -Payload @{ text = $t }
}

function Get-HookPayload {
    [CmdletBinding()]
    param()
    $path = $env:CI_HOOK_PAYLOAD
    if (-not $path) {
        throw 'Get-HookPayload: CI_HOOK_PAYLOAD is not set (not running inside a hook handler)'
    }
    Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-HookHeader {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0)][string]$Name)
    $path = $env:CI_HOOK_HEADERS
    if (-not $path -or -not (Test-Path -Path $path)) {
        return $null
    }
    $headers = Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $prop = $headers.PSObject.Properties | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    if ($prop) { return $prop.Value } else { return $null }
}

function Start-CiJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [hashtable]$Parameters = @{},
        [string]$DedupKey
    )
    $serverUrl = $env:CI_SERVER_URL
    if (-not $serverUrl) {
        throw 'Start-CiJob: CI_SERVER_URL is not set (not running inside a hook handler)'
    }
    $body = @{ parameters = $Parameters; dedupKey = $DedupKey } | ConvertTo-Json -Compress -Depth 4
    $uri = "$serverUrl/api/internal/start-job/$([Uri]::EscapeDataString($Name))"
    try {
        Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType 'application/json'
    }
    catch {
        throw "Start-CiJob: request failed: $($_.Exception.Message)"
    }
}

Export-ModuleMember -Function @(
    'Initialize-CiRunner',
    'Initialize-CiHandler',
    'Write-CiEvent',
    'Test-CiStageHandled',
    'Invoke-CiPostStages',
    'Stage',
    'PostStage',
    'Exec',
    'Register-JUnit',
    'Register-Artifact',
    'Set-CiEnv',
    'Set-BuildNote',
    'Get-HookPayload',
    'Get-HookHeader',
    'Start-CiJob'
)
