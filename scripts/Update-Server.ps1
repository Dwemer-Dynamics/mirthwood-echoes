[CmdletBinding()]
param(
  [string]$Package,
  [string]$Distro = 'ReignServer',
  [string]$GameDir,
  [string]$SaveDir = "$env:USERPROFILE\AppData\LocalLow\Bad Ridge Games\Mirthwood\SaveData"
)
$ErrorActionPreference='Stop'
if(!$Package){
  $Package=if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app\bundle\manifest.json')){$PSScriptRoot}else{Join-Path (Split-Path $PSScriptRoot) '.work\Mirthwood-Echoes-Server'}
}
if(!$GameDir){
  $steam=(Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam').SteamPath
  $vdf=Get-Content -LiteralPath (Join-Path $steam 'steamapps\libraryfolders.vdf') -Raw
  $libraries=@($steam)+@([regex]::Matches($vdf,'"path"\s+"([^"]+)"')|ForEach-Object{ $_.Groups[1].Value.Replace('\\','\') })
  $GameDir=$libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Mirthwood' } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Mirthwood.exe') } | Select-Object -First 1
}
if(!$GameDir){throw 'Steam could not locate Mirthwood. Supply -GameDir.'}
function Mounted([string]$value){
  $full=[IO.Path]::GetFullPath($value)
  if($full -notmatch '^([a-zA-Z]):\\'){throw "A local drive is required for $full"}
  '/mnt/'+$Matches[1].ToLowerInvariant()+'/'+$full.Substring(3).Replace('\','/')
}
$manifestPath=Join-Path $Package 'app\bundle\manifest.json'
if(!(Test-Path -LiteralPath $manifestPath)){throw 'Build or extract the Mirthwood: Echoes server package first.'}
$manifest=Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if($manifest.id -ne 'mirthwood-echoes' -or $manifest.name -cne 'Mirthwood: Echoes'){throw 'This package does not identify itself as Mirthwood: Echoes.'}
$displayName=$manifest.name
$launcherRoot=Join-Path $env:LOCALAPPDATA 'MirthwoodEchoes\Play'
$legacyRoot=Join-Path $env:LOCALAPPDATA 'MirthwoodAI\Play'
$desktop=[Environment]::GetFolderPath('DesktopDirectory')
$link=Join-Path $desktop 'Mirthwood Echoes.lnk'
$previousLink=Join-Path $desktop 'Play Mirthwood - Echoes.lnk'
$legacyLink=Join-Path $desktop 'Play Mirthwood AI.lnk'
$shell=New-Object -ComObject WScript.Shell
function Test-PlayLink([string]$path,[string]$root,[string]$exe){
  if(!(Test-Path -LiteralPath $path -PathType Leaf)){return $false}
  $target=$shell.CreateShortcut($path).TargetPath
  if(!$target -or [IO.Path]::GetFileName($target) -ne $exe){return $false}
  $dir=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target))
  return $dir -eq $root -or ([IO.Path]::GetDirectoryName($dir) -eq $root -and [IO.Path]::GetFileName($dir) -match '^[0-9A-F]{24}$')
}
if((Test-Path -LiteralPath $link) -and !(Test-PlayLink $link $launcherRoot 'Mirthwood.Echoes.Play.exe') -and !(Test-PlayLink $link $legacyRoot 'Mirthwood.Play.exe')){
  throw "A different shortcut already uses $link."
}
$sourceRoot=(Get-Item -LiteralPath (Join-Path $Package 'launcher')).FullName
$iconName='mirthwood-echoes.ico'
$sourceIcon=Join-Path $sourceRoot $iconName
if(!(Test-Path -LiteralPath $sourceIcon -PathType Leaf)){throw 'The Echoes package is missing its desktop shortcut icon.'}
$iconHash=(Get-FileHash -LiteralPath $sourceIcon).Hash
$linuxPackage=Mounted $Package
wsl.exe -d $Distro -u root --exec /bin/sh "$linuxPackage/install-service.sh" $linuxPackage (Mounted $GameDir) (Mounted $SaveDir)
if($LASTEXITCODE){throw 'The server update failed.'}
$sourceFiles=@(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object Extension -notin @('.pdb','.xml') | Sort-Object FullName)
$fileHashes=($sourceFiles | ForEach-Object { $_.FullName.Substring($sourceRoot.Length+1)+':'+(Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
$sha=[Security.Cryptography.SHA256]::Create()
try{$generation=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Distro+$fileHashes)))).Replace('-','').Substring(0,24)}finally{$sha.Dispose()}
$launch=Join-Path $launcherRoot $generation
[IO.Directory]::CreateDirectory($launch) | Out-Null
foreach($f in $sourceFiles){
  $out=Join-Path $launch $f.FullName.Substring($sourceRoot.Length+1)
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out))|Out-Null
  if(!(Test-Path -LiteralPath $out) -or (Get-FileHash -LiteralPath $f.FullName).Hash -ne (Get-FileHash -LiteralPath $out).Hash){
    $staged=$out+'.new'
    [IO.File]::Copy($f.FullName,$staged,$true)
    if(Test-Path -LiteralPath $out){[IO.File]::Replace($staged,$out,$null)}else{[IO.File]::Move($staged,$out)}
  }
}
[IO.File]::WriteAllText((Join-Path $launch 'distro.txt'),$Distro)
$shortcut=$shell.CreateShortcut($link)
$exe=Join-Path $launch 'Mirthwood.Echoes.Play.exe'
$icon=Join-Path $launch $iconName
if((Get-FileHash -LiteralPath $icon).Hash -ne $iconHash){throw 'Could not verify the installed Echoes shortcut icon.'}
$iconLocation="$icon,0"
$shortcut.TargetPath=$exe
$shortcut.WorkingDirectory=$launch
$shortcut.IconLocation=$iconLocation
$shortcut.Arguments=''
$shortcut.Description="Open $displayName."
$shortcut.Save()
$installedShortcut=$shell.CreateShortcut($link)
if($installedShortcut.TargetPath -ne $exe -or $installedShortcut.IconLocation -ne $iconLocation -or !(Test-Path -LiteralPath $exe)){throw 'Could not verify the Echoes desktop shortcut.'}
# Explorer supports a literal display label in the folder's LocalizedFileNames table.
# The profile API updates only this shortcut's entry and preserves other desktop customizations.
if(!('EchoesDesktopLabel' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EchoesDesktopLabel {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool WritePrivateProfileString(string section,string key,string value,string path);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
  public static extern void SHChangeNotify(int change,uint flags,string first,IntPtr second);
}
'@
}
$ini=Join-Path $desktop 'desktop.ini'
if(!(Test-Path -LiteralPath $ini)){[IO.File]::WriteAllText($ini,'',[Text.Encoding]::Unicode)}
$attrs=[IO.File]::GetAttributes($ini)
try {
  if($attrs.HasFlag([IO.FileAttributes]::ReadOnly)){[IO.File]::SetAttributes($ini,$attrs -bxor [IO.FileAttributes]::ReadOnly)}
  if(![EchoesDesktopLabel]::WritePrivateProfileString('LocalizedFileNames',[IO.Path]::GetFileName($link),$displayName,$ini)){throw 'Could not set the shortcut display name.'}
}finally{[IO.File]::SetAttributes($ini,$attrs -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System)}
$desktopInfo=Get-Item -LiteralPath $desktop -Force
$desktopInfo.Attributes=$desktopInfo.Attributes -bor [IO.FileAttributes]::ReadOnly
[EchoesDesktopLabel]::SHChangeNotify(0x2000,5,$link,[IntPtr]::Zero)
[EchoesDesktopLabel]::SHChangeNotify(0x1000,5,$desktop,[IntPtr]::Zero)
$labelDeadline=[datetime]::UtcNow.AddSeconds(8)
do{
  $explorer=New-Object -ComObject Shell.Application
  $folder=$explorer.Namespace($desktop)
  $item=$folder.ParseName([IO.Path]::GetFileName($link))
  if($item -and $item.Name -ceq $displayName){break}
  Start-Sleep -Milliseconds 250
}while([datetime]::UtcNow -lt $labelDeadline)
if(!$item -or $item.Name -cne $displayName){throw 'Explorer did not report the expected shortcut label.'}
if(Test-PlayLink $previousLink $launcherRoot 'Mirthwood.Echoes.Play.exe'){Remove-Item -LiteralPath $previousLink}
if(Test-PlayLink $legacyLink $legacyRoot 'Mirthwood.Play.exe'){Remove-Item -LiteralPath $legacyLink}
# Old launchers are generated files. Verify the foundation installer's fingerprints
# before retiring them, and leave any independently added files or running copy alone.
if((Test-Path -LiteralPath $legacyRoot -PathType Container) -and !(Get-Process -Name Mirthwood.Play -ErrorAction SilentlyContinue)){
  $legacyRootInfo=Get-Item -LiteralPath $legacyRoot
  if(!$legacyRootInfo.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){
    $oldDirs=@($legacyRootInfo)+@(Get-ChildItem -LiteralPath $legacyRoot -Directory | Where-Object Name -match '^[0-9A-F]{24}$')
    foreach($dir in $oldDirs){
      if($dir.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){continue}
      $settingsFile=Join-Path $dir.FullName 'distro.txt'
      $oldFiles=@(Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object Name -in @('Mirthwood.Play.deps.json','Mirthwood.Play.dll','Mirthwood.Play.exe','Mirthwood.Play.runtimeconfig.json') | Sort-Object Name)
      if($oldFiles.Count -ne 4 -or !(Test-Path -LiteralPath $settingsFile -PathType Leaf)){continue}
      $hashes=($oldFiles | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash }) -join ''
      $digestInput=if($dir.FullName -eq $legacyRoot){$hashes}else{[IO.File]::ReadAllText($settingsFile).Trim()+$hashes}
      $hasher=[Security.Cryptography.SHA256]::Create()
      try{$digest=([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($digestInput)))).Replace('-','')}finally{$hasher.Dispose()}
      $managed=if($dir.FullName -eq $legacyRoot){$digest -eq '11D404F3B0DA4165A1719C1AB9CAAA68C0B0A662EBEDD2F2ED64C89A33B99F1F'}else{$dir.Name -eq $digest.Substring(0,24)}
      if(!$managed){continue}
      foreach($file in $oldFiles){Remove-Item -LiteralPath $file.FullName}
      Remove-Item -LiteralPath $settingsFile
      if($dir.FullName -ne $legacyRoot -and !(Get-ChildItem -LiteralPath $dir.FullName -Force)){[IO.Directory]::Delete($dir.FullName,$false)}
    }
    if(!(Get-ChildItem -LiteralPath $legacyRoot -Force)){
      [IO.Directory]::Delete($legacyRoot,$false)
      $oldApp=[IO.Path]::GetDirectoryName($legacyRoot)
      if(!(Get-ChildItem -LiteralPath $oldApp -Force)){[IO.Directory]::Delete($oldApp,$false)}
    }
  }
}
# Remove only an old installer-created generation with no running launcher.
$running=@(Get-Process -Name Mirthwood.Echoes.Play -ErrorAction SilentlyContinue | ForEach-Object { $_.Path })
$generations=@(Get-ChildItem -LiteralPath $launcherRoot -Directory | Where-Object { $_.Name -cmatch '^[0-9A-F]{24}$' -and $_.FullName -ne $launch } | Sort-Object LastWriteTimeUtc -Descending)
foreach($old in @($generations | Select-Object -Skip 1)){
  $resolved=[IO.Path]::GetFullPath($old.FullName)
  if($old.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
  if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($launcherRoot)){throw 'Unexpected launcher generation path.'}
  if($running -contains (Join-Path $resolved 'Mirthwood.Echoes.Play.exe')){continue}
  Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Desktop shortcut: $displayName"
