# Renders c3o.svg to c3o.png (1920x1080), the plugin image that deploy.sh installs.
# Uses headless Microsoft Edge, so nothing extra needs installing. Run from PowerShell:
#   .\render.ps1
# Fonts come from Windows (Segoe UI), so render on Windows.
$ErrorActionPreference = 'Stop'
$dir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$svg  = Join-Path $dir 'c3o.svg'
$png  = Join-Path $dir 'c3o.png'
$url  = 'file:///' + ($svg -replace '\\', '/')

if (Test-Path $png) { Remove-Item $png -Confirm:$false }
$p = Start-Process -FilePath $edge -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
    '--headless', '--disable-gpu', '--hide-scrollbars', '--window-size=1920,1080',
    "--screenshot=`"$png`"", "`"$url`"")
if ($p.ExitCode -ne 0 -or -not (Test-Path $png)) { throw "Edge did not produce $png (exit $($p.ExitCode))" }
"Rendered $png ($((Get-Item $png).Length) bytes)"
