[CmdletBinding()]
param(
  [string]$GameDir = 'C:\SteamLibrary\steamapps\common\Mirthwood',
  [string]$LoaderZip = "$env:USERPROFILE\Downloads\BepInEx Mirthwood Pre-Configured (Windows)-1-5-4-23-2-1744307525.zip",
  [string]$Version,
  [string]$PackageDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$identity=([xml](Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup
if(!$Version){$Version=$identity.Version}
$work=Join-Path $root '.work'
$package=if($PackageDirectory){[IO.Path]::GetFullPath($PackageDirectory)}else{Join-Path $work 'Mirthwood-Echoes-Server'}
if ((Get-FileHash -LiteralPath $LoaderZip).Hash -ne '93DD0EB72106E42D84FCA0B35ADD863A59ABFB9A5D894D942DF4C41633CE5434') { throw 'The supplied loader differs from the verified Mirthwood archive.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$loader=Join-Path $work 'loader'
[IO.Directory]::CreateDirectory($loader) | Out-Null
$archive=[IO.Compression.ZipFile]::OpenRead($LoaderZip)
try {
  $payload=@($archive.Entries | Where-Object { $_.FullName -match '^BepInEx/core/[^/]+\.dll$|^(\.doorstop_version|doorstop_config\.ini|winhttp\.dll|BepInEx/config/BepInEx\.cfg)$' })
  foreach($entry in $payload){
    $local=Join-Path $loader $entry.FullName
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($local)) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$local,$true)
  }
  dotnet build (Join-Path $root 'module') -c Release "-p:GameDir=$GameDir" "-p:LoaderCore=$loader\BepInEx\core" "-p:Version=$Version" -m:1 -p:UseSharedCompilation=false --nologo
  if($LASTEXITCODE){throw 'Module build failed.'}
  dotnet publish (Join-Path $root 'server') -c Release "-p:Version=$Version" -m:1 -p:UseSharedCompilation=false -p:UseAppHost=false -o (Join-Path $package 'app') --nologo
  if($LASTEXITCODE){throw 'Server build failed.'}
  Expand-Archive -LiteralPath (Join-Path $root 'voices/Mirthwood-Echoes-Alignment-linux-x64.zip') -DestinationPath (Join-Path $package 'app') -Force
  dotnet publish (Join-Path $root 'launcher') -c Release "-p:Version=$Version" -m:1 -p:UseSharedCompilation=false -o (Join-Path $package 'launcher') --nologo
  if($LASTEXITCODE){throw 'Launcher build failed.'}
  $bundle=Join-Path $package 'app\bundle'
  $modulePath='BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll'
  $voicesPath='BepInEx/plugins/MirthwoodEchoes/Mirthwood-Echoes-Voices.zip'
  $cast=Get-Content -Raw -LiteralPath (Join-Path $root 'voices/cast.json') | ConvertFrom-Json
  $referencePaths=@((@($cast.voices.id)+'narrator') | ForEach-Object { "BepInEx/plugins/MirthwoodEchoes/Voices/$_.wav" })
  $manifest=@(foreach($path in (@($payload | ForEach-Object FullName)+$modulePath+$voicesPath+$referencePaths)){
    $target=Join-Path $bundle $path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    $source=if($path -eq $modulePath){Join-Path $root 'module\bin\Release\net472\Mirthwood.Echoes.dll'}elseif($path -eq $voicesPath){Join-Path $root 'voices\Mirthwood-Echoes-Voices.zip'}elseif($path -in $referencePaths){Join-Path $root ('voices/references/'+[IO.Path]::GetFileName($path))}else{Join-Path $loader $path}
    [IO.File]::Copy($source,$target,$true)
    $kind=if($path -eq $modulePath){'module'}elseif($path -eq $voicesPath -or $path -in $referencePaths){'voices'}elseif($path -in 'doorstop_config.ini','BepInEx/config/BepInEx.cfg'){'default'}else{'loader'}
    [ordered]@{path=$path;sha256=(Get-FileHash -LiteralPath $target).Hash;kind=$kind}
  })
  $json=[ordered]@{id=$identity.ModId;name=$identity.Product;version=$Version;files=$manifest}|ConvertTo-Json -Depth 5
  [IO.File]::WriteAllText((Join-Path $bundle 'manifest.json'),$json)
}finally{$archive.Dispose()}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-service.sh') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'web-control.py') -Destination (Join-Path $package 'app/web-control.py') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upgrade-identity.py') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Update-Server.ps1') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-Game.ps1') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $package -Force
Write-Output "Built $($identity.Product) $Version at $package"
