param(
    [Parameter(Mandatory)][string]$ReleaseZip
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Run this script with Windows PowerShell 5.1 (powershell.exe).'
}

$expectedHash = '045706A2911FA9208E1285C85F30FFE4519E192ACEE4BCB07B11E92F5AB47BD1'
$archivePath = (Resolve-Path -LiteralPath $ReleaseZip).ProviderPath
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The ZIP does not match the published Lacuna V1.2.0 Windows x64 release.'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $app = $archive.GetEntry('Lacuna.exe')
    if ($null -eq $app) { throw 'The release ZIP has no Lacuna.exe entry.' }
    $memory = New-Object IO.MemoryStream
    $stream = $app.Open()
    try {
        $stream.CopyTo($memory)
        # Reflection-only loading exposes the resources without executing program code.
        $assembly = [Reflection.Assembly]::ReflectionOnlyLoad($memory.ToArray())
    } finally {
        $stream.Dispose()
        $memory.Dispose()
    }
} finally {
    $archive.Dispose()
}

function Save-BuildDependency([IO.Stream]$InputStream, [string]$Path) {
    try {
        if ([IO.File]::Exists($Path)) { return }
        $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            $destination = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
            try { $InputStream.CopyTo($destination) }
            finally { $destination.Dispose() }
            [IO.File]::Move($temporary, $Path)
        } finally {
            [IO.File]::Delete($temporary)
        }
    } finally {
        $InputStream.Dispose()
    }
}

$root = Split-Path -Parent $PSScriptRoot
$pdf = [IO.Directory]::CreateDirectory((Join-Path $root 'third-party/PDF')).FullName
$ocr = [IO.Directory]::CreateDirectory((Join-Path $root 'third-party/RapidOCR')).FullName
$pdfNames = @($assembly.GetManifestResourceNames() | Where-Object { $_ -match '^PdfLibraries\.[A-Za-z0-9_.-]+\.dll$' })
if (!$pdfNames.Count) { throw 'The main program has no embedded PDF DLLs.' }
foreach ($name in $pdfNames) {
    $filename = $name.Substring('PdfLibraries.'.Length)
    Save-BuildDependency ($assembly.GetManifestResourceStream($name)) (Join-Path $pdf $filename)
}

$ocrResource = $assembly.GetManifestResourceStream('RapidOCR.zip')
if ($null -eq $ocrResource) { throw 'The main program has no embedded RapidOCR archive.' }
try {
    $ocrZip = New-Object IO.Compression.ZipArchive($ocrResource, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        foreach ($entry in $ocrZip.Entries) {
            if ($entry.FullName -match '^[A-Za-z0-9_.-]+\.(dll|exe|onnx)$') {
                Save-BuildDependency ($entry.Open()) (Join-Path $ocr $entry.Name)
            }
        }
    } finally {
        $ocrZip.Dispose()
    }
} finally {
    $ocrResource.Dispose()
}

Write-Output 'Restored the V1.2.0 PDF and OCR build dependencies.'
