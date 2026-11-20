[CmdletBinding()]
param([switch]$Play,[string]$Distro='ReignServer',[string]$GameDir)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EchoesWindowTest {
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window,int command);
}
'@
$base='http://127.0.0.1:17841'
$desktop=[Environment]::GetFolderPath('DesktopDirectory')
$link=Join-Path $desktop 'Mirthwood Echoes.lnk'
$shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($link)
$target=$shortcut.TargetPath
$icon=Join-Path (Split-Path -Parent $target) 'mirthwood-echoes.ico'
if($shortcut.IconLocation -ne "$icon,0" -or !(Test-Path -LiteralPath $icon -PathType Leaf)){throw 'Wrong Echoes shortcut icon.'}
$folder=(New-Object -ComObject Shell.Application).Namespace($desktop)
if($folder.ParseName('Mirthwood Echoes.lnk').Name -cne 'Mirthwood: Echoes'){throw 'Wrong Explorer display name.'}
if(Get-Process -Name Mirthwood.Echoes.Play,Mirthwood -ErrorAction SilentlyContinue){throw 'Close Mirthwood and its companion before this lifecycle test.'}
$gameExe=$null
if($Play){
    if(!$GameDir){
        $steam=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam').SteamPath
        $vdf=Get-Content -LiteralPath (Join-Path $steam 'steamapps\libraryfolders.vdf') -Raw
        $libraries=@($steam)+@([regex]::Matches($vdf,'"path"\s+"([^"]+)"') | ForEach-Object {$_.Groups[1].Value.Replace('\\','\')})
        $GameDir=$libraries | ForEach-Object {Join-Path $_ 'steamapps\common\Mirthwood'} | Where-Object {Test-Path -LiteralPath (Join-Path $_ 'Mirthwood.exe')} | Select-Object -First 1
    }
    if(!$GameDir){throw 'Steam could not locate Mirthwood. Supply -GameDir.'}
    $gameExe=(Get-Item -LiteralPath (Join-Path $GameDir 'Mirthwood.exe')).FullName
}

function Wait-For([scriptblock]$probe,[string]$detail,[int]$seconds=35){
    $end=[datetime]::UtcNow.AddSeconds($seconds)
    do{
        try{$value=& $probe;if($value){return $value}}catch{}
        Start-Sleep -Milliseconds 350
    }while([datetime]::UtcNow -lt $end)
    throw "Timed out waiting for $detail."
}
function Control {Invoke-RestMethod "$base/api/control" -TimeoutSec 2}
function Open-Window {
    Start-Process -FilePath $link -WindowStyle Normal
    $window=Wait-For {Get-Process -Name Mirthwood.Echoes.Play -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $target -and $_.MainWindowHandle -ne 0}} 'the installed companion'
    $state=Wait-For {Control} 'the visible Web UI'
    if($state.running){throw 'Opening the shortcut also started the backend.'}
    $window
}
function Ui-Button($window,[string]$label){
    $root=[Windows.Automation.AutomationElement]::FromHandle($window.MainWindowHandle)
    $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$label)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
}
function Press($window,[string]$label){
    $button=Wait-For {Ui-Button $window $label} $label
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Start-Backend {
    Invoke-RestMethod "$base/api/control/server" -Method Post -ContentType application/json -Body '{"running":true}' -TimeoutSec 30|Out-Null
}
function Wait-Off {
    Wait-For {
        $units=@(wsl.exe -d $Distro -- systemctl show -p MainPID --value mirthwood-echoes.service mirthwood-echoes-console.service |Where-Object {$_ -match '^\d+$'})
        $units.Count -eq 2 -and $units[0] -eq '0' -and $units[1] -eq '0'
    } 'both Echoes processes to exit'|Out-Null
    foreach($port in @(17841,17842)){
        $answered=$false
        try{Invoke-WebRequest "http://127.0.0.1:$port/api/health" -SkipHttpErrorCheck -TimeoutSec 1|Out-Null;$answered=$true}catch{}
        if($answered){throw "An Echoes listener still answered on $port."}
    }
}
function Close-TestGame($game){
    if($null -eq $game -or $game.HasExited){return}
    if($game.Path -ne $gameExe){throw 'Unexpected test game executable.'}
    [void]$game.CloseMainWindow()
    if(!$game.WaitForExit(30000)){throw 'The test game has not closed normally.'}
}
$app=$null;$game=$null
try{
    $app=Open-Window
    $null=Wait-For {(Ui-Button $app 'Start Game').Current.HasKeyboardFocus} 'initial Start Game selection'
    if(Get-Process -Name Mirthwood -ErrorAction SilentlyContinue){throw 'Opening the shortcut started Mirthwood.'}
    'PASS Explorer label, UI-only shortcut, backend off and initial Start Game focus.'
    if($Play){
        Press $app 'Start Game'
        $game=Wait-For {Get-Process -Name Mirthwood -ErrorAction SilentlyContinue|Where-Object Path -eq $gameExe} 'Steam to launch the game' 100
        $heartbeat=Wait-For {
            $diag=Invoke-RestMethod "$base/api/diagnostics" -TimeoutSec 3
            if($diag.game -and $diag.game.version -eq $diag.installation.version){$diag.game}
        } 'the matching module heartbeat' 75
        $again=Invoke-RestMethod "$base/api/control/game" -Method Post -ContentType application/json -Body '{}'
        if($again.state -ne 'running' -or @(Get-Process -Name Mirthwood).Count -ne 1){throw 'The repeated request did not preserve the existing game.'}
        "PASS native Start Game button, Steam process and loaded Echoes $($heartbeat.version); repeated launch kept one game."
    }else{Start-Backend}
    Press $app 'Exit'
    if(!$app.WaitForExit(20000)){throw 'Exit did not close the companion.'}
    Wait-Off
    if($game -and $game.HasExited){throw 'Exit closed the running game.'}
    'PASS Exit closes the companion and both listeners.'
    Close-TestGame $game
    $app=Open-Window;Start-Backend
    [void][EchoesWindowTest]::ShowWindowAsync($app.MainWindowHandle,6)
    Wait-Off
    [void][EchoesWindowTest]::ShowWindowAsync($app.MainWindowHandle,9)
    $null=Wait-For {(Control).running} 'the restored backend'
    [void]$app.CloseMainWindow()
    if(!$app.WaitForExit(20000)){throw 'The window close control did not finish.'}
    Wait-Off
    'PASS minimize shuts both listeners down, restore resumes, and closing the window stops both.'
    $app=Open-Window;Start-Backend
    if($app.Path -ne $target){throw 'Unexpected test process.'}
    $app.Kill();$app.WaitForExit()
    Wait-Off
    'PASS abrupt loss of the test window automatically shuts both listeners down.'
}finally{
    if($app -and !$app.HasExited -and $app.Path -eq $target){[void]$app.CloseMainWindow();[void]$app.WaitForExit(15000)}
    Close-TestGame $game
}
