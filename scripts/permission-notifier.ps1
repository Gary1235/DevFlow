param(
    [Parameter(Mandatory = $true)][string]$ApiUrl,
    [Parameter(Mandatory = $true)][string]$WebUrl,
    [Parameter(Mandatory = $true)][int]$OwnerProcessId
)
$ErrorActionPreference = 'Stop'
$origin = [Uri]$ApiUrl
if ($origin.Scheme -ne 'http' -or $origin.Host -notin @('127.0.0.1', 'localhost', '::1') -or $origin.AbsolutePath -ne '/') {
    throw 'Notifier only accepts a local HTTP origin.'
}
$webOrigin = [Uri]$WebUrl
if ($webOrigin.Scheme -ne 'http' -or $webOrigin.Host -notin @('127.0.0.1', 'localhost', '::1') -or $webOrigin.AbsolutePath -ne '/') {
    throw 'Notifier only opens a local HTTP web origin.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$owner = Get-Process -Id $OwnerProcessId
$ownerStarted = $owner.StartTime
$icon = New-Object System.Windows.Forms.NotifyIcon
$icon.Icon = [System.Drawing.SystemIcons]::Information
$icon.Text = 'DevFlow 工具核准通知'
$icon.Visible = $true
$script:seen = @{}
$script:destination = $WebUrl
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 5000
$timer.add_Tick({
    try {
        $currentOwner = Get-Process -Id $OwnerProcessId -ErrorAction Stop
        if ($currentOwner.StartTime -ne $ownerStarted) { [System.Windows.Forms.Application]::Exit(); return }
    } catch { [System.Windows.Forms.Application]::Exit(); return }
    try {
        $pending = @(Invoke-RestMethod -Uri ($ApiUrl.TrimEnd('/') + '/api/permissions/pending') -TimeoutSec 2)
        $active = @{}
        foreach ($request in $pending) {
            $key = [string]$request.id
            $active[$key] = $true
            if ($script:seen.ContainsKey($key)) { continue }
            $script:seen[$key] = $true
            $identifier = [Guid]::Empty
            if ($request.kind -notin @('task', 'discussion') -or -not [Guid]::TryParse([string]$request.ownerId, [ref]$identifier)) { continue }
            $script:destination = $WebUrl.TrimEnd('/') + '/#' + $request.kind + '/' + $identifier.ToString()
            $body = [string]$request.title + ' · ' + [string]$request.reason
            if ($body.Length -gt 250) { $body = $body.Substring(0, 250) }
            $icon.ShowBalloonTip(15000, 'DevFlow 等待工具核准', $body, [System.Windows.Forms.ToolTipIcon]::Info)
        }
        $script:seen = $active
        $icon.Text = 'DevFlow 待核准 ' + $pending.Count
    } catch { # Transient API failures keep existing deduplication state.
    }
})
$icon.add_BalloonTipClicked({
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $script:destination
    $start.UseShellExecute = $true
    [System.Diagnostics.Process]::Start($start) | Out-Null
})
try { $timer.Start(); [System.Windows.Forms.Application]::Run() }
finally { $timer.Stop(); $timer.Dispose(); $icon.Visible = $false; $icon.Dispose() }
