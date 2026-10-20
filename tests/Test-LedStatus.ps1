param([string]$Configuration = 'Release', [string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = if ($AssemblyPath) { $AssemblyPath } else { Join-Path $PSScriptRoot "../src/MLS203.Control/bin/x64/$Configuration/net48/MLS203.Control.exe" }
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $assembly))
$app = New-Object Mls203.Control.App
$app.InitializeComponent()
$window = New-Object Mls203.Control.MainWindow
$method = $window.GetType().GetMethod('UpdateStatusIndicators', [Reflection.BindingFlags]'Instance,NonPublic')
function Assert-Led([uint32]$Bits, [string]$AxisColor, [string]$PowerColor) {
    $snapshot = New-Object Mls203.Control.Core.StageStatusSnapshot
    $snapshot.XStatusBits = $snapshot.YStatusBits = [Enum]::ToObject([Thorlabs.MotionControl.XA.UniversalStatusBits], $Bits)
    [void]$method.Invoke($window, [object[]]@($snapshot.PSObject.BaseObject))
    foreach ($name in @('XEnableLed', 'YEnableLed', 'PowerStatusLed')) {
        $expected = if ($name -eq 'PowerStatusLed') { $PowerColor } else { $AxisColor }
        if ($window.FindName($name).Fill.ToString() -ne $expected) { throw "$name mismatch for status $Bits" }
    }
}
# Real UMC2 Y telemetry: Interlock=true, Enabled=true, Error=false.
Assert-Led 0x90F12500L '#FF008000' '#FF008000'
# Same healthy telemetry with enable cleared; then moving and homing.
Assert-Led 0x10F12500 '#FF0000FF' '#FF008000'
Assert-Led 0x90F12510L '#FFFFA500' '#FF008000'
Assert-Led 0x90F12700L '#FFFFA500' '#FF008000'
# Real UMC2 X telemetry: actual faults must remain red, even during motion.
Assert-Led 0x58F92100 '#FFFF0000' '#FF008000'
Assert-Led 0x58F92110 '#FFFF0000' '#FF008000'
if ($window.FindName('XEnableLed').ToolTip -notmatch 'CommutationError.*BusCurrentFault') { throw 'Fault details missing' }
Assert-Led 0 '#FF808080' '#FF808080'
# Tracking alone does not mean physical motion.
Assert-Led 0x90F13500L '#FF008000' '#FF008000'
Assert-Led 0x100 '#FF800080' '#FF0000FF'
# A voltage fault is a real supply alarm.
Assert-Led 0x10040100 '#FFFF0000' '#FFFF0000'
# Initializing must alternate blue and light gray.
$snapshot = New-Object Mls203.Control.Core.StageStatusSnapshot
$snapshot.XStatusBits = $snapshot.YStatusBits = [Enum]::ToObject([Thorlabs.MotionControl.XA.UniversalStatusBits], [uint32]0x10000900)
[void]$method.Invoke($window, [object[]]@($snapshot.PSObject.BaseObject))
$first = $window.FindName('XEnableLed').Fill.ToString()
[void]$method.Invoke($window, [object[]]@($snapshot.PSObject.BaseObject))
$second = $window.FindName('XEnableLed').Fill.ToString()
if (@($first,$second) -notcontains '#FF0000FF' -or @($first,$second) -notcontains '#FFD3D3D3') { throw 'Blink failed' }
# Power must remain blue during initialization, even with PowerOk set.
if ($window.FindName('PowerStatusLed').Fill.ToString() -ne '#FF0000FF') { throw 'Initializing power must be blue' }
# Motor faults remain red on axes without overriding healthy power.
$snapshot.XStatusBits = [Enum]::ToObject([Thorlabs.MotionControl.XA.UniversalStatusBits], [uint32]0x58F92100)
$snapshot.YStatusBits = [Enum]::ToObject([Thorlabs.MotionControl.XA.UniversalStatusBits], [uint32]0x90F12500L)
[void]$method.Invoke($window, [object[]]@($snapshot.PSObject.BaseObject))
if ($window.FindName('XEnableLed').Fill.ToString() -ne '#FFFF0000' -or
    $window.FindName('YEnableLed').Fill.ToString() -ne '#FF008000' -or
    $window.FindName('PowerStatusLed').Fill.ToString() -ne '#FF008000') { throw 'Independent axis colors failed' }
# Velocity is motion evidence even when directional flags are absent.
$snapshot.XStatusBits = $snapshot.YStatusBits
$snapshot.XVelocity = -1
[void]$method.Invoke($window, [object[]]@($snapshot.PSObject.BaseObject))
if ($window.FindName('XEnableLed').Fill.ToString() -ne '#FFFFA500') { throw 'Velocity must show motion' }
# Unavailable data must clear all old colors.
$reset = $window.GetType().GetMethod('ResetStatusIndicators', [Reflection.BindingFlags]'Instance,NonPublic')
[void]$reset.Invoke($window, $null)
foreach ($name in @('XEnableLed','YEnableLed','PowerStatusLed')) {
    if ($window.FindName($name).Fill.ToString() -ne '#FF808080') { throw 'Unavailable status must be gray' }
}
$window.Close()
$app.Shutdown()
'LED regression checks passed (no controller connection or motion).'



