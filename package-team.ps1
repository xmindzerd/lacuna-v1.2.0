param(
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$Legacy1155,
    [Parameter(Mandatory)][string]$Legacy116,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-Entries([string]$folder) {
    $root = (Get-Item -LiteralPath $folder).FullName.TrimEnd('\')
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName) {
        [ordered]@{path=$file.FullName.Substring($root.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    }
}

function Get-OfficialEntries([string]$folder, [string]$digest) {
    $entries = @(Get-Entries $folder)
    [string[]]$rows = @($entries | ForEach-Object { $_.path + [char]9 + $_.sha256 + [char]10 })
    [Array]::Sort($rows,[StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes(($rows -join '')))).Replace('-','') }
    finally { $sha.Dispose() }
    if ($actual -ne $digest) { throw "旧版目录与正式 ZIP 的文件清单不一致：$folder" }
    $entries
}

function Write-JsonFile($value, [string]$file) {
    [IO.File]::WriteAllText($file,(ConvertTo-Json -InputObject $value -Depth 5),$utf8)
}

function Test-ZipFiles([string]$archive, [string]$folder) {
    $expected = @{}
    foreach ($item in Get-Entries $folder) { $expected.Add($item.path,$item.sha256) }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            $key = $entry.FullName.Replace('\','/')
            $stream = $entry.Open()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose() }
            if ($expected[$key] -ne $hash) { throw "ZIP 文件校验失败：$key" }
            $expected.Remove($key)
        }
        if ($expected.Count) { throw "ZIP 缺少文件：$($expected.Keys -join ', ')" }
    } finally { $sha.Dispose(); $zip.Dispose() }
}

$release = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Destination)).FullName
$package = Join-Path $release 'Lacuna-V1.2.0-Windows-x64'
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath ($package+'.zip'))) { throw '目标包已存在，请指定新的输出目录。' }
# Canonical path + SHA-256 lists from the two published Windows ZIPs.
$old55 = @(Get-OfficialEntries $Legacy1155 'DCC079677348B0D296282505BA1A686AD72298CCB600C798F4453CC60827CD4F')
$old6 = @(Get-OfficialEntries $Legacy116 '97DC9B3E143C70F1ACE3BE5230BC823923D3DFDEDDFFE6D254ED80694BA9CC61')
$app = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'build/Lacuna.exe')
if ($app.VersionInfo.ProductVersion -cne 'V1.2.0') { throw '请先编译 V1.2.0 主程序。' }
$stage = [IO.Directory]::CreateDirectory((Join-Path $release ('.stage-'+[Guid]::NewGuid().ToString('N')))).FullName
$payloadFolder = [IO.Directory]::CreateDirectory((Join-Path $stage 'package')).FullName
foreach ($dir in 'tools','licenses') { Copy-Item -LiteralPath (Join-Path $Runtime $dir) -Destination $payloadFolder -Recurse }
foreach ($name in '第三方组件许可.txt','团队使用说明.txt') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $payloadFolder }
Copy-Item -LiteralPath $app.FullName -Destination $payloadFolder
$manifest = [ordered]@{ product='Lacuna'; version='V1.2.0'; platform='windows-x64'; entry='Lacuna.exe'
    compatible_sources=@('V1.1.55','V1.1.6','V1.2.0'); files=@(Get-Entries $payloadFolder) }
Write-JsonFile $manifest (Join-Path $stage 'payload.json')
Write-JsonFile $old55 (Join-Path $stage 'legacy1155.json')
Write-JsonFile $old6 (Join-Path $stage 'legacy116.json')
$resources = @('payload','legacy1155','legacy116') | ForEach-Object { '/resource:' + (Join-Path $stage ($_+'.json')) + ',' + $_ }
$fw = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$installer = Join-Path $payloadFolder '升级一键成片.exe'
& "$fw/csc.exe" /nologo /langversion:5 /warnaserror /platform:x64 /target:winexe /optimize+ /utf8output /codepage:65001 "/out:$installer" ('/win32icon:'+(Join-Path $PSScriptRoot 'Brand.ico')) "/reference:$fw/System.Windows.Forms.dll" "/reference:$fw/System.Web.Extensions.dll" "/reference:$fw/Microsoft.CSharp.dll" @resources (Join-Path $PSScriptRoot 'TeamInstaller.cs')
if ($LASTEXITCODE) { throw '升级程序编译失败。' }
$manifest.files += @([ordered]@{path='升级一键成片.exe';sha256=(Get-FileHash -LiteralPath $installer).Hash})
Write-JsonFile $manifest (Join-Path $payloadFolder 'install-manifest.json')
$archive = Join-Path $stage 'package.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payloadFolder,$archive,[IO.Compression.CompressionLevel]::Optimal,$false,[Text.Encoding]::UTF8)
Test-ZipFiles $archive $payloadFolder
[IO.Directory]::Move($payloadFolder,$package)
[IO.File]::Move($archive,($package+'.zip'))
foreach ($name in 'payload','legacy1155','legacy116') {
    [IO.File]::Copy((Join-Path $stage ($name+'.json')),(Join-Path $app.DirectoryName ($name+'.json')),$true)
    [IO.File]::Delete((Join-Path $stage ($name+'.json')))
}
[IO.Directory]::Delete($stage)
Get-FileHash -LiteralPath ($package+'.zip')
