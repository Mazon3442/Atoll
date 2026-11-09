# ATOLL agent integration: tells the island when a command that ran 10 seconds or longer finishes.
# Loaded from your PowerShell profile. Remove it in ATOLL Settings › Agents.
if (-not (Test-Path variable:global:AtollPreviousPrompt)) { $global:AtollPreviousPrompt = $function:prompt }

function global:prompt {
    $ok = $?
    $exitCode = $global:LASTEXITCODE
    $last = Get-History -Count 1
    if ($last -and $last.Id -ne $global:AtollLastCommand) {
        $global:AtollLastCommand = $last.Id
        $seconds = [int]($last.EndExecutionTime - $last.StartExecutionTime).TotalSeconds
        if ($seconds -ge 10) {
            $OutputEncoding = [Text.UTF8Encoding]::new($false)
            $code = if ($ok) { 0 } elseif ($exitCode) { $exitCode } else { 1 }
            $took = if ($seconds -ge 60) { '{0}m {1}s' -f [int][math]::Floor($seconds / 60), ($seconds % 60) } else { "$seconds s" }
            try {
                @{ hook_event_name = 'CommandFinished'; session_id = "$PID"; cwd = "$PWD"; command = $last.CommandLine
                   notification_type = "$code"; message = $took } |
                    ConvertTo-Json -Compress | & __ATOLL_HOOK_EXE__ --agent shell
            } catch { }
            $global:LASTEXITCODE = $exitCode
        }
    }
    if ($global:AtollPreviousPrompt) { & $global:AtollPreviousPrompt }
    else { "PS $($executionContext.SessionState.Path.CurrentLocation)$('>' * ($nestedPromptLevel + 1)) " }
}
