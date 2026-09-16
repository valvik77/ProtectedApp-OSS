# ScheduledTasks PowerShell cmdlets use CIM, which may be unavailable in
# Windows Sandbox even when the Task Scheduler service itself is running.
# The Task Scheduler COM API is the native local interface used here instead.
function Get-GuardianTaskScheduler {
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    return $scheduler
}

function Get-GuardianHealthTask {
    param([Parameter(Mandatory)][string]$TaskName)

    $folder = (Get-GuardianTaskScheduler).GetFolder('\')
    try {
        return $folder.GetTask($TaskName)
    }
    catch {
        # HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND): this is a fresh install.
        if ($_.Exception.HResult -eq -2147024894) { return $null }
        throw
    }
}

function Stop-GuardianHealthTask {
    param([Parameter(Mandatory)][string]$TaskName)

    $task = Get-GuardianHealthTask $TaskName
    if ($null -ne $task -and $task.State -eq 4) { $task.Stop(0) }
}

function Start-GuardianHealthTask {
    param([Parameter(Mandatory)][string]$TaskName)

    $task = Get-GuardianHealthTask $TaskName
    if ($null -eq $task) { throw "No se encuentra la tarea de Guardian: $TaskName" }
    if ($task.State -ne 4) { $null = $task.Run($null) }
}

function Set-GuardianHealthTaskEnabled {
    param([Parameter(Mandatory)][string]$TaskName, [Parameter(Mandatory)][bool]$Enabled)

    $task = Get-GuardianHealthTask $TaskName
    if ($null -ne $task) { $task.Enabled = $Enabled }
}

function Remove-GuardianHealthTask {
    param([Parameter(Mandatory)][string]$TaskName)

    $scheduler = Get-GuardianTaskScheduler
    $folder = $scheduler.GetFolder('\')
    if ($null -ne (Get-GuardianHealthTask $TaskName)) { $folder.DeleteTask($TaskName, 0) }
}

function Register-GuardianHealthTask {
    param(
        [Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$AppPath
    )

    $scheduler = Get-GuardianTaskScheduler
    $folder = $scheduler.GetFolder('\')
    $definition = $scheduler.NewTask(0)
    $definition.RegistrationInfo.Description = 'Rearma las puertas, bloquea la sesión y reinicia ProtectedApp Guardian si se detiene.'
    $definition.Principal.UserId = 'SYSTEM'
    $definition.Principal.LogonType = 5 # TASK_LOGON_SERVICE_ACCOUNT
    $definition.Principal.RunLevel = 1 # TASK_RUNLEVEL_HIGHEST
    $definition.Settings.Enabled = $true

    $boot = $definition.Triggers.Create(8) # TASK_TRIGGER_BOOT
    $boot.Enabled = $true
    $timer = $definition.Triggers.Create(1) # TASK_TRIGGER_TIME
    $timer.StartBoundary = (Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
    $timer.Repetition.Interval = 'PT1M'
    $timer.Repetition.StopAtDurationEnd = $false
    $timer.Enabled = $true

    $action = $definition.Actions.Create(0) # TASK_ACTION_EXEC
    $action.Path = $ExecutablePath
    $action.Arguments = '--health-watch --app "{0}"' -f $AppPath

    $definition.Settings.StartWhenAvailable = $true
    $definition.Settings.MultipleInstances = 2 # TASK_INSTANCES_IGNORE_NEW
    $definition.Settings.ExecutionTimeLimit = 'PT0S'
    $definition.Settings.RestartCount = 3
    $definition.Settings.RestartInterval = 'PT1M'
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false

    # TASK_CREATE_OR_UPDATE = 6. SYSTEM has no password.
    $null = $folder.RegisterTaskDefinition($TaskName, $definition, 6, 'SYSTEM', $null, 5)
}
