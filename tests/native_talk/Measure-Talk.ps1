[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportDirectory,
      [string]$GameDir='C:\SteamLibrary\steamapps\common\Mirthwood',
      [ValidateSet('World','All','Walkthrough')][string]$Mode='World',
      [string]$TalkTestRun,
      [ValidateRange(0,299)][int]$Slot=0)
$ErrorActionPreference='Stop'
if(Get-Process -Name Mirthwood -ErrorAction SilentlyContinue){throw 'The probe needs its own isolated Mirthwood process.'}
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out=[IO.Directory]::CreateDirectory($ReportDirectory).FullName
$probe='Mirthwood.Echoes.NativeTalkProbe.dll'
$dest=Join-Path $GameDir "BepInEx\plugins\$probe"
$source=Join-Path $PSScriptRoot "bin\Release\net472\$probe"
$latest=Join-Path $env:LOCALAPPDATA 'MirthwoodEchoes\Diagnostics\latest-native-talk.json'
dotnet build (Join-Path $PSScriptRoot 'NativeTalkProbe.csproj') -c Release "-p:GameDir=$GameDir" -m:1 -p:UseSharedCompilation=false --nologo --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Could not build the isolated native Talk probe.'}
$digest=(Get-FileHash -LiteralPath $source).Hash
[IO.File]::Copy($source,$dest,$false)
try{
    if($Mode -eq 'Walkthrough'){
        & (Join-Path $root 'scripts\Test-Game.ps1') -Mode Walkthrough -IsolatedSaveSlot $Slot -TalkTestRun $TalkTestRun -TimeoutSeconds 320 -Report (Join-Path $out 'input.json')
        return
    }
    $started=[DateTime]::UtcNow
    & (Join-Path $root 'scripts\Test-Game.ps1') -Mode $Mode -IsolatedSaveSlot $Slot -Report (Join-Path $out 'world.json')
    if(!(Test-Path -LiteralPath $latest) -or (Get-Item -LiteralPath $latest).LastWriteTimeUtc -lt $started){throw 'No fresh native Talk report.'}
    Copy-Item -LiteralPath $latest -Destination (Join-Path $out 'native-talk.json')
    $report=Get-Content -Raw -LiteralPath $latest | ConvertFrom-Json
    if($report.error){throw $report.error}
    $menu=$report.menu
    if($menu.beforeGreeting[0] -ne 'Talk' -or $menu.beforeGreeting -notcontains 'Greet' -or $menu.beforeGreeting -notcontains 'Jeer' -or $menu.afterGreeting[0] -ne 'Talk' -or
       !$menu.sameOptions -or !$menu.otherCallbacksPreserved -or !$menu.correctTarget -or !$menu.guarded -or !$menu.visible -or !$menu.paused){throw 'Native Talk option check failed.'}
    if($report.scoring.Count -ne 30 -or @($report.scoring | Where-Object {!$_.matches -or $_.romanceDelta -ne 0 -or $_.happinessDelta -ne 0}).Count){throw 'Native scorer check failed.'}
    $signs=@($report.scoring | ForEach-Object {[Math]::Sign($_.delta)} | Sort-Object -Unique)
    if(($signs -join ',') -ne '-1,0,1'){throw 'The tested scores did not cover positive, negative and neutral friendship.'}
    Write-Host "Passed native Talk selection and $($report.scoring.Count) friendship checks for $($report.resident)."
}finally{
    if((Test-Path -LiteralPath $dest) -and (Get-FileHash -LiteralPath $dest).Hash -eq $digest -and !(Get-Process -Name Mirthwood -ErrorAction SilentlyContinue)){
        [IO.File]::Delete($dest)
    }
}
