[CmdletBinding()]
param(
    [ValidateSet('Runtime','Interface','World','Provider','Relationships','SocialBalance','Quests','Interactions','Voices','Walkthrough','All')][string]$Mode='Runtime',
    [switch]$Launch,
    [ValidateRange(0,299)][int]$IsolatedSaveSlot,
    [ValidateRange(15,600)][int]$TimeoutSeconds=135,
    [string]$TalkTestRun,
    [string]$Report
)
$ErrorActionPreference='Stop'
$base='http://127.0.0.1:17841'
$isolated=$PSBoundParameters.ContainsKey('IsolatedSaveSlot')
if($Mode -eq 'Provider' -and !$isolated){throw 'The live provider check requires -IsolatedSaveSlot.'}
if($Mode -eq 'SocialBalance' -and !$isolated){throw 'The native balance comparison requires -IsolatedSaveSlot.'}
if($Mode -eq 'Voices' -and !$isolated){throw 'The voice playback check requires -IsolatedSaveSlot.'}
if($Mode -in 'Relationships','Quests','Interactions','Walkthrough'){
    if(!$isolated -or $TalkTestRun -cnotmatch '^[a-f0-9]{32}$'){throw 'Run tests/relationships.py with the isolated backend and save.'}
    if((Invoke-RestMethod 'http://127.0.0.1:17864/api/test-session' -TimeoutSec 4).run -cne $TalkTestRun){throw 'The disposable Talk database does not match this run.'}
}
$runId=''
$runDir=$null
$openedWindow=$false
$startedBackend=$false
if($isolated -and (Get-Process -Name Mirthwood -ErrorAction SilentlyContinue)){throw 'Close Mirthwood before starting a separate isolated test run.'}
try {
$control=$null
try{$control=Invoke-RestMethod "$base/api/control" -TimeoutSec 2}catch{}
if(($Launch -or $isolated) -and !$control){
    $link=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Mirthwood Echoes.lnk'
    if(!(Test-Path -LiteralPath $link -PathType Leaf)){throw 'The Echoes desktop shortcut is missing. Run the server updater.'}
    $openedWindow=!(Get-Process -Name Mirthwood.Echoes.Play -ErrorAction SilentlyContinue)
    Start-Process -FilePath $link
    for($i=0;$i -lt 45 -and !$control;$i++){
        Start-Sleep -Seconds 1
        try{$control=Invoke-RestMethod "$base/api/control" -TimeoutSec 2}catch{}
    }
}
if(!$control){throw 'Open the Mirthwood: Echoes desktop window before running the game harness.'}
if($isolated -and !$control.running){
    Invoke-RestMethod "$base/api/control/server" -Method Post -ContentType application/json -Body '{"running":true}'|Out-Null
    $startedBackend=$true
}elseif($Launch -and !$isolated){
    Invoke-RestMethod "$base/api/control/game" -Method Post -ContentType application/json -Body '{}'|Out-Null
}elseif(!$control.running){throw 'Turn on the server in the Echoes window before running the game harness.'}
if($isolated){
    $installation=Invoke-RestMethod "$base/api/module/check" -Method Post -ContentType application/json -Body '{}' -TimeoutSec 45
    if($installation.status -ne 'ready'){throw "The installed test module is unavailable. $($installation.detail)"}
    $saveDir=Join-Path $env:USERPROFILE 'AppData\LocalLow\Bad Ridge Games\Mirthwood\SaveData'
    $source=Join-Path $saveDir ("Slot"+$IsolatedSaveSlot+".savegame")
    if(!(Test-Path -LiteralPath $source -PathType Leaf)){throw 'The chosen source slot has no main save file.'}
    $steam=(Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam').SteamExe
    if(!(Test-Path -LiteralPath $steam -PathType Leaf)){throw 'Steam executable unavailable.'}
    $runId=if($Mode -in 'Relationships','Quests','Interactions','Walkthrough'){$TalkTestRun}else{[guid]::NewGuid().ToString('N')}
    $harnessRoot=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MirthwoodEchoes\Harness'))
    $runDir=Join-Path $harnessRoot $runId
    $copies=Join-Path $runDir 'SaveData';[IO.Directory]::CreateDirectory($copies)|Out-Null
    $originals=@{}
    foreach($suffix in @('','.b')){
        $file=$source+$suffix
        if(Test-Path -LiteralPath $file -PathType Leaf){
            $target=Join-Path $copies ('Slot0.savegame'+$suffix)
            $originals[$file]=(Get-FileHash -LiteralPath $file).Hash
            [IO.File]::Copy($file,$target,$false)
            if((Get-FileHash -LiteralPath $target).Hash -ne $originals[$file]){throw 'Test copy failed verification.'}
        }
    }
    $manifest=@{id=$runId;sourceSlot=$IsolatedSaveSlot;sha256=$originals[$source]}
    if($Mode -in 'Relationships','Quests','Interactions','Walkthrough'){$manifest.talkPort=17864}
    [IO.File]::WriteAllText((Join-Path $runDir 'harness.json'),($manifest|ConvertTo-Json -Compress))
    if($Mode -eq 'Walkthrough'){[IO.File]::WriteAllText((Join-Path $runDir 'walkthrough.input'),$runId)}
    Start-Process -FilePath $steam -ArgumentList @('-applaunch','2272900','-echoes-harness',$runId) -WindowStyle Hidden
    Write-Host "Steam is loading a verified copy of slot $IsolatedSaveSlot."
}
if($Mode -eq 'Walkthrough'){
    # The optional, test-only input probe runs in this copied game. Save its observations before cleanup.
    $finish=Join-Path $runDir 'walkthrough.finish'
    Write-Host "Hands-on copied save $runId. Finish by writing its ID to $finish."
    $until=[datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while([datetime]::UtcNow -lt $until){
        if((Test-Path -LiteralPath $finish -PathType Leaf) -and [IO.File]::ReadAllText($finish).Trim() -ceq $runId){
            [IO.File]::Delete($finish)
            if($Report){
                $observed=Join-Path $runDir 'walkthrough.json'
                if(Test-Path -LiteralPath $observed -PathType Leaf){
                    $out=[IO.Path]::GetFullPath($Report)
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out))
                    [IO.File]::Copy($observed,$out,$true)
                    foreach($name in 'walkthrough-menu.png','walkthrough-draft.png','walkthrough-reply.png'){
                        $capture=Join-Path $runDir $name
                        if(Test-Path -LiteralPath $capture -PathType Leaf){
                            [IO.File]::Copy($capture,(Join-Path ([IO.Path]::GetDirectoryName($out)) $name),$true)
                            [IO.File]::Delete($capture)
                        }
                    }
                    [IO.File]::Delete($observed)
                    $proof=Get-Content -Raw -LiteralPath $out|ConvertFrom-Json
                    if($proof.error){throw $proof.error}
                }
            }
            return
        }
        Start-Sleep -Seconds 1
    }
    throw 'The hands-on copied-save session ended before the player finished.'
}
$modes=if($Mode -eq 'All'){@('Runtime','World','Interface')}else{@($Mode)}
$results=@()
foreach($check in $modes){
    $task=Invoke-RestMethod "$base/api/diagnostics/probes" -Method Post -ContentType application/json -Body (@{mode=$check.ToLowerInvariant();run=$runId}|ConvertTo-Json -Compress)
    Write-Host "Waiting for the $check check inside Mirthwood…"
    if($check -in 'World','Interface'){Write-Host 'This check needs a loaded save with a resident on screen.'}
    $deadline=[datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    do{
        Start-Sleep -Seconds 1
        $task=Invoke-RestMethod "$base/api/diagnostics/probes/$($task.id)" -TimeoutSec 5
    }while($task.state -in 'waiting','running' -and [datetime]::UtcNow -lt $deadline)
    $results+=@($task)
    foreach($line in $task.result.checks){Write-Host ("  {0}  {1}  {2}" -f $(if($line.passed){'PASS'}else{'FAIL'}),$line.name,$line.detail)}
    if($task.state -ne 'passed'){break}
}
if($Report){
    $file=[IO.Path]::GetFullPath($Report);[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))|Out-Null
    [IO.File]::WriteAllText($file,(ConvertTo-Json -InputObject $results -Depth 14),[Text.UTF8Encoding]::new($false))
}
if(@($results|Where-Object state -ne 'passed').Count){throw "Game check $($task.mode) ended in state $($task.state)."}
Write-Host "$($modes.Count) in-game check(s) passed."
} finally {
    if($isolated -and $runDir -and (Test-Path -LiteralPath (Join-Path $runDir 'harness.json'))){
        [IO.File]::WriteAllText((Join-Path $runDir 'quit.request'),$runId)
        $until=[datetime]::UtcNow.AddSeconds(30)
        do {
            $own=@(Get-CimInstance Win32_Process -Filter "Name='Mirthwood.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($runId) })
            if($own.Count){Start-Sleep -Milliseconds 500}
        }while($own.Count -and [datetime]::UtcNow -lt $until)
        foreach($file in $originals.Keys){
            if((Get-FileHash -LiteralPath $file).Hash -ne $originals[$file]){throw 'Source save bytes changed during the isolated test. The test copies were retained for inspection.'}
        }
        if($own.Count){Write-Warning "The isolated process is still closing. Its copy is at $runDir."}
        elseif([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($runDir)) -eq $harnessRoot -and (Get-Content -Raw -LiteralPath (Join-Path $runDir 'harness.json')|ConvertFrom-Json).id -eq $runId){
            $pilot=Join-Path $runDir 'walkthrough.input'
            if($Mode -eq 'Walkthrough' -and [IO.File]::Exists($pilot) -and [IO.File]::ReadAllText($pilot) -ceq $runId){[IO.File]::Delete($pilot)}
            $pending=Join-Path $runDir 'PendingTalk'
            if((Test-Path -LiteralPath $pending -PathType Container) -and !(Get-Item -LiteralPath $pending).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){
                foreach($file in Get-ChildItem -LiteralPath $pending -File){
                    if(($file.Name -eq 'write.lock' -or $file.Name -cmatch '^[a-f0-9]{32}-[a-f0-9]{32}\.json(\.tmp)?$') -and !$file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){[IO.File]::Delete($file.FullName)}
                }
                if(@(Get-ChildItem -LiteralPath $pending -Force).Count -eq 0){[IO.Directory]::Delete($pending,$false)}
            }
            foreach($folder in @($copies,(Join-Path $runDir 'TempSaveData'))){
                if(!(Test-Path -LiteralPath $folder -PathType Container)){continue}
                $directory=Get-Item -LiteralPath $folder -Force
                if($directory.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){continue}
                foreach($entry in Get-ChildItem -LiteralPath $folder -File -Force){
                    $owned=$entry.Name -in @('Slot0.savegame','Slot0.savegame.b','Slot0.metadata','Slot0.metadata.b') -or $entry.Name -cmatch '^slot0_extra_backup \d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.json$'
                    if($owned -and !$entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){[IO.File]::Delete($entry.FullName)}
                }
                if(@(Get-ChildItem -LiteralPath $folder -Force).Count -eq 0){[IO.Directory]::Delete($folder,$false)}
            }
            $left=@(Get-ChildItem -LiteralPath $runDir -Force | Where-Object { $_.Name -notin @('harness.json','quit.request') })
            $events=Join-Path $runDir 'NativeEvents'
            if((Test-Path -LiteralPath $events -PathType Container) -and !(Get-Item -LiteralPath $events).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and @(Get-ChildItem -Force -LiteralPath $events).Count -eq 0){
                [IO.Directory]::Delete($events,$false)
                $left=@($left | Where-Object Name -ne 'NativeEvents')
            }
            $receipts=Join-Path $runDir 'QuestReceipts'
            if((Test-Path -LiteralPath $receipts -PathType Container) -and !(Get-Item -LiteralPath $receipts).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and @(Get-ChildItem -Force -LiteralPath $receipts).Count -eq 0){
                [IO.Directory]::Delete($receipts,$false)
                $left=@($left | Where-Object Name -ne 'QuestReceipts')
            }
            if($left.Count){Write-Warning "The isolated run retained unrecognized files at $runDir."}
            else {
                foreach($name in @('harness.json','quit.request')){[IO.File]::Delete((Join-Path $runDir $name))}
                [IO.Directory]::Delete($runDir,$false)
            }
        }
        Write-Host 'Verified the original save hashes after the test.'
    }
    if($isolated -and ($openedWindow -or $startedBackend)){
        try{
            if($openedWindow){Invoke-RestMethod "$base/api/control/exit" -Method Post -ContentType application/json -Body '{}'|Out-Null}
            else{Invoke-RestMethod "$base/api/control/server" -Method Post -ContentType application/json -Body '{"running":false}'|Out-Null}
        }catch{Write-Verbose 'The companion is already closed.'}
    }
}

