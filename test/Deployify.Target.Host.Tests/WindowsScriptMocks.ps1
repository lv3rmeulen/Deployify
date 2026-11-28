# Stand-ins for the Windows-only commands deployify-host.ps1 uses, so WindowsScriptTests can run
# the script with PowerShell 7 on any OS. The tests insert these into the script's server block,
# where they replace the cmdlets and the script's own Windows-only helpers. The services' state
# lives in the JSON file named by DEPLOYIFY_MOCK_STATE.

function Get-MockState {
    if (Test-Path $env:DEPLOYIFY_MOCK_STATE) { return (Get-Content -Raw -Path $env:DEPLOYIFY_MOCK_STATE | ConvertFrom-Json -AsHashtable) }
    return @{ services = @{}; events = @(); calls = @() }
}

function Save-MockState($state) {
    $state | ConvertTo-Json -Depth 10 | Set-Content -Path $env:DEPLOYIFY_MOCK_STATE
}

function Add-MockCall([string]$call) {
    $state = Get-MockState
    $state.calls = @($state.calls) + $call
    Save-MockState $state
}

function Test-Administrator { return $true }
function Protect-ServiceKey([string]$key) { Add-MockCall "protect $($key.Split('\')[-1])" }
function New-PrivateFolder([string]$dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
function Register-EventSource([string]$source) { Add-MockCall "event source $source" }

function New-ItemProperty($Path, $Name, $PropertyType, $Value, [switch]$Force) {
    $state = Get-MockState
    $state.services[$Path.Split('\')[-1]].environment = @($Value)
    Save-MockState $state
}

function Remove-ItemProperty($Path, $Name, $ErrorAction) {
    $state = Get-MockState
    $state.services[$Path.Split('\')[-1]].environment = @()
    Save-MockState $state
}

function Get-CimInstance($ClassName, $Filter) {
    if ($Filter -notmatch "Name='(.+)'") { return $null }
    $name = $Matches[1]
    $s = (Get-MockState).services[$name]
    if ($null -eq $s) { return $null }
    return [pscustomobject]@{ Name = $name; State = $s.state; ProcessId = $s.processId; ExitCode = $s.exitCode; PathName = $s.path }
}

function Invoke-CimMethod($InputObject, $MethodName, $Arguments) {
    $state = Get-MockState
    $s = $state.services[$InputObject.Name]
    if ($Arguments.ContainsKey('PathName')) { $s.path = $Arguments.PathName; $s.display = $Arguments.DisplayName }
    if ($Arguments.ContainsKey('StartName')) { $s.account = $Arguments.StartName }
    Save-MockState $state
    return [pscustomobject]@{ ReturnValue = 0 }
}

function New-Service($Name, $BinaryPathName, $DisplayName, $StartupType) {
    $state = Get-MockState
    $state.services[$Name] = @{ state = 'Stopped'; processId = 0; exitCode = 0; path = $BinaryPathName; display = $DisplayName; environment = @() }
    Save-MockState $state
    return [pscustomobject]@{ Name = $Name }
}

function Get-Service($Name, $ErrorAction) {
    $s = (Get-MockState).services[$Name]
    if ($null -eq $s) {
        if ($ErrorAction -eq 'SilentlyContinue') { return $null }
        throw "Cannot find any service with service name '$Name'."
    }
    $service = [pscustomobject]@{ Name = $Name; Status = $s.state }
    $service | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value { param($status, $timeout) }
    return $service
}

function Stop-Service($Name, [switch]$Force) {
    $state = Get-MockState
    $state.services[$Name].state = 'Stopped'
    $state.services[$Name].processId = 0
    Save-MockState $state
}

# Starts the 'process': a .exe whose content says fail-to-start behaves like an app without UseWindowsService().
function Start-Service($Name) {
    $state = Get-MockState
    $s = $state.services[$Name]
    $exe = $s.path.Trim('"')
    if (-not (Test-Path $exe)) { throw "Service '$Name' cannot be started: $exe is missing." }
    if ((Get-Content -Raw -Path $exe) -match 'fail-to-start') {
        throw "Service '$Name' cannot be started due to the following error: The service did not respond to the start or control request in a timely fashion."
    }
    $s.state = 'Running'
    $s.processId = [int]$env:DEPLOYIFY_MOCK_PID
    Save-MockState $state
}

function sc.exe {
    Add-MockCall ("sc " + ($args -join ' '))
    if ($args[0] -eq 'delete') {
        $state = Get-MockState
        $state.services.Remove($args[1])
        Save-MockState $state
    }
}

function Get-WinEvent($FilterHashtable, $MaxEvents, $ErrorAction) {
    $events = @((Get-MockState).events | Where-Object { $_.log -eq $FilterHashtable.LogName -and $_.provider -eq $FilterHashtable.ProviderName })
    if ($events.Count -eq 0) { throw 'No events were found that match the specified selection criteria.' }
    return @($events | ForEach-Object {
        [pscustomobject]@{ RecordId = [long]$_.recordId; TimeCreated = [datetime]$_.time; Message = $_.message; Level = [int]$_.level }
    } | Sort-Object RecordId -Descending | Select-Object -First $MaxEvents)
}

