<#
    Smoke-captures the running application: launches it, walks the left-hand navigation, saves one PNG per
    page, then closes it.

    WHY THIS EXISTS: a WPF application compiles whether or not its XAML resources, bindings and templates
    actually resolve — a missing StaticResource key or a DataTemplate that maps no view-model only fails at
    runtime, when the window is first shown. Running the real executable and looking at the real window is
    the only check that covers that class of defect.

    RUN WITH WINDOWS POWERSHELL 5.1 (powershell.exe), not pwsh: UIAutomationClient and System.Drawing are
    .NET Framework assemblies and are not present in PowerShell 7.

    Every file this script writes stays inside the repository (artifacts/).
#>
[CmdletBinding()]
param(
    [string] $Exe,
    [string] $OutputDirectory,
    [string[]] $Pages = @('Dashboard', 'Games', 'Mods', 'Settings'),
    [int] $StartupTimeoutSeconds = 40,
    [int] $SettleMilliseconds = 1500
)

# These defaults are computed here rather than in the param block: $PSScriptRoot is not yet populated while
# parameter default values are being evaluated.
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $repositoryRoot 'src\ModManager.App\bin\Debug\net8.0-windows\ModManager.exe'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$automation = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]

function Wait-ForMainWindow {
    param([int] $ProcessId, [int] $TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            $automation::ProcessIdProperty, $ProcessId)
        $window = $automation::RootElement.FindFirst($scope::Children, $condition)
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 400
    }

    return $null
}

function Save-WindowScreenshot {
    param($Window, [string] $Path)

    $rect = $Window.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        throw "The window reported an empty bounding rectangle; nothing to capture."
    }

    $bitmap = New-Object System.Drawing.Bitmap([int] $rect.Width, [int] $rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int] $rect.X, [int] $rect.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    return $Path
}

function Select-Page {
    param($Window, [string] $Name)

    $condition = New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $Name)
    $element = $Window.FindFirst($scope::Descendants, $condition)
    if ($null -eq $element) {
        Write-Warning "No navigation element named '$Name' was found."
        return $false
    }

    # A WPF RadioButton is a selection item, but the exact pattern available through UI Automation varies,
    # so each applicable pattern is tried in turn rather than assuming one.
    foreach ($pattern in @(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [System.Windows.Automation.TogglePattern]::Pattern,
            [System.Windows.Automation.InvokePattern]::Pattern)) {
        if (-not $element.TryGetCurrentPattern($pattern, [ref] $null)) { continue }
        $instance = $element.GetCurrentPattern($pattern)
        if ($pattern -eq [System.Windows.Automation.SelectionItemPattern]::Pattern) { $instance.Select() }
        elseif ($pattern -eq [System.Windows.Automation.TogglePattern]::Pattern) { $instance.Toggle() }
        else { $instance.Invoke() }
        return $true
    }

    Write-Warning "Element '$Name' exposed no usable automation pattern."
    return $false
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "Executable not found: $Exe" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Write-Host "Launching $Exe"
$process = Start-Process -FilePath $Exe -PassThru

try {
    $window = Wait-ForMainWindow -ProcessId $process.Id -TimeoutSeconds $StartupTimeoutSeconds
    if ($null -eq $window) { throw "The application did not present a window within $StartupTimeoutSeconds seconds." }

    $result = [ordered]@{
        ProcessId = $process.Id
        WindowTitle = $window.Current.Name
        Captures = @()
        MissingPages = @()
    }

    $index = 0
    foreach ($page in $Pages) {
        $index++
        if ($index -gt 1 -and -not (Select-Page -Window $window -Name $page)) {
            $result.MissingPages += $page
            continue
        }

        Start-Sleep -Milliseconds $SettleMilliseconds
        $file = Join-Path $OutputDirectory ('{0:00}-{1}.png' -f $index, $page.ToLowerInvariant())
        Save-WindowScreenshot -Window $window -Path $file | Out-Null
        $result.Captures += $file
        Write-Host "Captured $page -> $file"
    }

    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'capture.json')
    Write-Host "Capture complete."
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(8000)) { $process.Kill() }
    }
}
