param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $Runtime = "win-x64",

    [string] $Configuration = "Release",

    [string] $ArtifactsDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifacts = Join-Path $root $ArtifactsDirectory
$publishDir = Join-Path $artifacts "publish\pbw"
$zipPath = Join-Path $artifacts "pbw_${Version}_windows_x64.zip"
$checksumPath = "$zipPath.sha256"

Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir | Out-Null
New-Item -ItemType Directory -Path $artifacts -ErrorAction SilentlyContinue | Out-Null

dotnet publish (Join-Path $root "src\Pbw.Cli\Pbw.Cli.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version `
    -o $publishDir

Get-ChildItem -Path $publishDir -Filter "*.pdb" | Remove-Item -Force

Remove-Item -Force $zipPath, $checksumPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -NoNewline -Encoding ASCII $checksumPath

Write-Output $zipPath
Write-Output $checksumPath
