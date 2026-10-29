param()

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\SpotifyOverlay\SpotifyOverlay.csproj'
$publishFolder = Join-Path $PSScriptRoot 'artifacts\publish'

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false --output $publishFolder

if ($LASTEXITCODE -ne 0) {
    throw 'The build failed.'
}

Write-Host "Built $publishFolder\SpotifyOverlay.exe"
