param(
    [string]$XaPath = ""
)

$ErrorActionPreference = "Stop"
$projectDirectory = Join-Path $PSScriptRoot "src\MLS203.Control"
$destination = Join-Path $projectDirectory "lib"
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$searchRoots = @()
if ($XaPath) {
    $searchRoots += $XaPath
}
if ($env:ProgramFiles) {
    $searchRoots += (Join-Path $env:ProgramFiles "Thorlabs XA")
    $searchRoots += (Join-Path $env:ProgramFiles "Thorlabs")
}
if (${env:ProgramFiles(x86)}) {
    $searchRoots += (Join-Path ${env:ProgramFiles(x86)} "Thorlabs XA")
    $searchRoots += (Join-Path ${env:ProgramFiles(x86)} "Thorlabs")
}
$searchRoots = $searchRoots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

if (-not $searchRoots) {
    throw "Thorlabs XA is not installed. Download the x64 XA Software/SDK from https://www.thorlabs.com/software_pages/ViewSoftwarePage.cfm?Code=Motion_Control, then rerun this script."
}

function Find-XaFile([string]$name) {
    foreach ($root in $searchRoots) {
        $match = Get-ChildItem -Path $root -Filter $name -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object @{ Expression = { if ($_.FullName -match '[\\/]x64[\\/]') { 0 } else { 1 } } },
                        @{ Expression = 'LastWriteTime'; Descending = $true } |
            Select-Object -First 1
        if ($match) { return $match }
    }
    return $null
}

$dotnet = Find-XaFile "tlmc_xa_dotnet.dll"
$native = Find-XaFile "tlmc_xa_native.dll"
if (-not $dotnet -or -not $native) {
    throw "XA SDK DLLs were not found (Kinesis alone is not sufficient for UMC2). Install the x64 XA Software/SDK, or run: .\setup-xa.ps1 -XaPath 'C:\path\to\XA'."
}

Copy-Item $dotnet.FullName (Join-Path $destination $dotnet.Name) -Force
Copy-Item $native.FullName (Join-Path $destination $native.Name) -Force
Write-Host "XA SDK copied from the installed Thorlabs package."
Write-Host "  .NET:  $($dotnet.FullName)"
Write-Host "  Native: $($native.FullName)"
Write-Host "Open MLS203-UMC2-Control.sln, select x64, then build."
