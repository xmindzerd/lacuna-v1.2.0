param(
    [Parameter(Mandatory)][string]$DotNet,
    [Parameter(Mandatory)][string]$Scratch
)
$ErrorActionPreference='Stop'
$work=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Scratch)).FullName
$env:DOTNET_CLI_HOME=Join-Path $work 'dotnet'
$env:NUGET_PACKAGES=Join-Path $work 'nuget'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $work 'http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:TEMP=$env:TMP=[IO.Directory]::CreateDirectory((Join-Path $work 'temp')).FullName
$publish=Join-Path $work 'ocr-native'
& $DotNet publish (Join-Path $PSScriptRoot 'Lacuna.Ocr.csproj') --artifacts-path (Join-Path $work 'native-build') -o $publish -c Release
if($LASTEXITCODE){throw 'OCR publish failed'}
# The main app embeds the checked-in runtime. Publish here to independently verify or rebuild its two generated files.
Get-Item (Join-Path $publish 'Lacuna.Ocr.exe'),(Join-Path $publish 'OpenCvSharpExtern.dll')
