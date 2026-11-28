param(
    [string]$Executable = (Join-Path $PSScriptRoot '../build/Lacuna.exe'),
    [string]$Scratch = [IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
$run = [IO.Directory]::CreateDirectory((Join-Path $Scratch ('channel-edit-' + [Guid]::NewGuid().ToString('N')))).FullName
$oldSettings = $env:ONE_CLICK_FILM_SETTINGS
$env:ONE_CLICK_FILM_SETTINGS = $run

try {
    Add-Type -AssemblyName System.Security
    $app = [Reflection.Assembly]::LoadFrom((Get-Item -LiteralPath $Executable).FullName)
    $flags = [Reflection.BindingFlags]'Static,NonPublic'
    $imageProfiles = $app.GetType('OneClickFilm.ImageApiProfiles', $true)
    $get = $imageProfiles.GetMethod('Get', $flags)
    $store = $imageProfiles.GetMethod('Store', $flags)
    $readKey = $imageProfiles.GetMethod('Key', $flags)
    $id = 'image-api-' + [Guid]::NewGuid().ToString('N')
    $otherId = 'image-api-' + [Guid]::NewGuid().ToString('N')
    $firstUrl = 'https://old.invalid/v1'
    $nextUrl = 'https://new.invalid/v1'
    $firstKey = [Guid]::NewGuid().ToString('N')
    $nextKey = [Guid]::NewGuid().ToString('N')
    $settingsFile = Join-Path $run '图片平台.json'
    $keys = [IO.Directory]::CreateDirectory((Join-Path $run '本机凭证')).FullName
    $legacy = @{ Id=$id; Name='Fixture'; Driver='images'; BaseUrl=$firstUrl; ImageModel='fixture-model' }
    $unrelated = $legacy.Clone()
    $unrelated.Id = $otherId
    [IO.File]::WriteAllText($settingsFile, (ConvertTo-Json -InputObject @($legacy,$unrelated)))
    $encrypted = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($firstKey), $null, 'CurrentUser')
    [IO.File]::WriteAllBytes((Join-Path $keys ($id + '.dpapi')), $encrypted)

    function Assert-That([bool]$ok, [string]$label) {
        if (!$ok) { throw $label }
        Write-Output ('PASS ' + $label)
    }
    function Commit-Edit([string]$url, [string]$key) {
        $draft = $get.Invoke($null, @($id))
        $draft.BaseUrl = $url
        $store.Invoke($null, @($draft,$key))
    }
    function Read-Current {
        $get.Invoke($null, @($id))
    }
    $stale = Read-Current
    Assert-That ($readKey.Invoke($null, @($stale)) -ceq $firstKey) 'Original PR credential is readable'
    Commit-Edit $firstUrl ''
    $rejected = $false
    try { Commit-Edit $nextUrl '' } catch { $rejected = $_.Exception.GetBaseException() -is [ArgumentException] }
    Assert-That $rejected 'Changing the endpoint requires its key'
    Assert-That ((Read-Current).BaseUrl -ceq $firstUrl) 'Rejected edit keeps the old endpoint'

    # Hold the JSON open without delete sharing to fail the atomic replace after the key has been staged.
    $fileLock = [IO.File]::Open($settingsFile,'Open','Read','Read')
    try {
        $rejected = $false
        try { Commit-Edit $nextUrl $nextKey } catch { $rejected = $_.Exception.GetBaseException() -is [IO.IOException] }
        Assert-That $rejected 'Failed metadata publication is reported'
    } finally { $fileLock.Dispose() }
    $unchanged = Read-Current
    Assert-That ($unchanged.BaseUrl -ceq $firstUrl -and $readKey.Invoke($null,@($unchanged)) -ceq $firstKey) 'Old address and key remain paired after failure'
    Assert-That (@(Get-ChildItem -LiteralPath $keys -Filter '*.dpapi').Count -eq 1) 'Failed edit discards its staged key'

    Commit-Edit $nextUrl $nextKey
    $changed = Read-Current
    Assert-That ($changed.Id -ceq $id -and $changed.BaseUrl -ceq $nextUrl -and $readKey.Invoke($null,@($changed)) -ceq $nextKey) 'Original project ID resolves the new endpoint and key'
    $saved = Get-Content -LiteralPath $settingsFile -Encoding UTF8 -Raw | ConvertFrom-Json
    Assert-That ($saved.Count -eq 2 -and $get.Invoke($null,@($otherId)).BaseUrl -ceq $firstUrl) 'A distinct same-name channel is preserved'
    $staleRejected = $false
    try { $null = $readKey.Invoke($null, @($stale)) } catch { $staleRejected = $_.Exception.GetBaseException() -is [InvalidOperationException] }
    Assert-That $staleRejected 'A stale address cannot read the new key'
    Commit-Edit $nextUrl ''
    Assert-That ($readKey.Invoke($null, @((Read-Current))) -ceq $nextKey) 'Saving the same URL with an empty key preserves it'
    Commit-Edit $firstUrl $firstKey
    Assert-That ((Read-Current).BaseUrl -ceq $firstUrl -and @(Get-ChildItem -LiteralPath $keys -Filter '*.dpapi').Count -eq 1) 'Switching back keeps one live credential'
} finally {
    $env:ONE_CLICK_FILM_SETTINGS = $oldSettings
}
