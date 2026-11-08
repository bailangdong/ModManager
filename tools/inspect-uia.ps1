<#
    Enumerates every top-level window of a freshly launched application process and, for each one, prints
    its title, class, rectangle and child-element count. Used to tell a real main window apart from an
    error dialog or a hidden WPF message window.

    RUN WITH WINDOWS POWERSHELL 5.1 (powershell.exe): UIAutomationClient is a .NET Framework assembly.
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [int] $StartupTimeoutSeconds = 40,
    [int] $SettleSeconds = 5,
    [int] $MaxChildren = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $repositoryRoot 'src\ModManager.App\bin\Debug\net8.0-windows\ModManager.exe'
}

$automation = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]

$process = Start-Process -FilePath $Exe -PassThru
try {
    $handle = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and $handle -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 400
        $live = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($null -eq $live) { throw "The process exited before it presented a window." }
        $live.Refresh()
        $handle = $live.MainWindowHandle
    }

    if ($handle -eq [IntPtr]::Zero) { throw "No main window handle appeared within $StartupTimeoutSeconds seconds." }
    Write-Host "MAIN WINDOW HANDLE: $handle"

    Start-Sleep -Seconds $SettleSeconds

    $condition = New-Object System.Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $process.Id)
    $tops = $automation::RootElement.FindAll($scope::Children, $condition)
    Write-Host "TOP-LEVEL WINDOWS FOR PID $($process.Id): $($tops.Count)"

    for ($i = 0; $i -lt $tops.Count; $i++) {
        $top = $tops.Item($i)
        $rect = $top.Current.BoundingRectangle
        Write-Host ('[{0}] name="{1}" class="{2}" rect={3},{4} {5}x{6} offscreen={7}' -f `
            $i, $top.Current.Name, $top.Current.ClassName, `
            [Math]::Round($rect.X), [Math]::Round($rect.Y), [Math]::Round($rect.Width), [Math]::Round($rect.Height), `
            $top.Current.IsOffscreen)

        $children = $top.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        Write-Host "    descendants: $($children.Count)"
        $limit = [Math]::Min($MaxChildren, $children.Count)
        for ($j = 0; $j -lt $limit; $j++) {
            $child = $children.Item($j)
            $name = $child.Current.Name
            if ($name.Length -gt 70) { $name = $name.Substring(0, 70) + '...' }
            Write-Host ('      {0,-38} "{1}"' -f $child.Current.ControlType.ProgrammaticName, $name)
        }
    }
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(8000)) { $process.Kill() }
    }
}
