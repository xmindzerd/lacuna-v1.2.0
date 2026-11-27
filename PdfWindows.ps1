$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
$null = [Windows.Data.Pdf.PdfPageRenderOptions,Windows.Data.Pdf,ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$genericTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and
        $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
$actionTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and !$_.IsGenericMethod -and $_.GetParameters().Length -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
} | Select-Object -First 1

function Get-Result($operation, [Type]$type) {
    $task = $genericTask.MakeGenericMethod($type).Invoke($null,@($operation))
    $task.GetAwaiter().GetResult()
}

function Render-Page($document,[int]$number,[int]$longEdge) {
    $page = $document.GetPage([uint32]($number-1))
    try {
        $options = [Windows.Data.Pdf.PdfPageRenderOptions]::new()
        $scale = $longEdge / [Math]::Max(1.0, [Math]::Max($page.Size.Height,$page.Size.Width))
        $options.DestinationWidth = [uint32][Math]::Max(1, [Math]::Round($page.Size.Width * $scale))
        $options.DestinationHeight = [uint32][Math]::Max(1, [Math]::Round($page.Size.Height * $scale))
        $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
        try {
            $task = $actionTask.Invoke($null, @($page.RenderToStreamAsync($stream,$options)))
            $null = $task.GetAwaiter().GetResult()
            $stream.Seek(0)
            return $stream
        } catch {
            $stream.Dispose()
            throw
        }
    } finally { $page.Dispose() }
}

function Is-VisuallyBlank($stream) {
    $stream.Seek(0)
    $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream)
    $picture = [System.Drawing.Bitmap]::new($net)
    try {
        $thumb = [System.Drawing.Bitmap]::new($picture,120,120)
        try {
            $dark = 0
            for($y=0;$y -lt 120;$y++){
                for($x=0;$x -lt 120;$x++){
                    $p = $thumb.GetPixel($x,$y)
                    if($p.R -lt 232 -or $p.G -lt 232 -or $p.B -lt 232){ $dark++ }
                }
            }
            return $dark -le 3
        } finally { $thumb.Dispose() }
    } finally { $picture.Dispose() }
}

try {
    $argsPdf = ([Console]::In.ReadToEnd()).TrimStart([char]0xFEFF) | ConvertFrom-Json
    $phase = 'load'
    $inputFile = [IO.File]::OpenRead([string]$argsPdf.file)
    $file = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($inputFile)
    $document = Get-Result ([Windows.Data.Pdf.PdfDocument]::LoadFromStreamAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    if($argsPdf.mode -eq 'preview') {
        $phase = 'render'
        $render = Render-Page $document ([int]$argsPdf.page) 1450
        try {
            $read = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($render)
            $buffer = [IO.MemoryStream]::new()
            try {
                $read.CopyTo($buffer)
                [Console]::WriteLine([Convert]::ToBase64String($buffer.ToArray()))
            } finally { $buffer.Dispose() }
        } finally { $render.Dispose() }
        exit 0
    }

    $phase = 'RapidOCR'
    Add-Type -AssemblyName System.Drawing
    $root=[string]$argsPdf.ocrRoot
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Join-Path $root 'Lacuna.Ocr.exe'
    $start.WorkingDirectory=$root
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding=$start.StandardOutputEncoding
    $engine=[Diagnostics.Process]::Start($start)
    $errors=$engine.StandardError.ReadToEndAsync()
    $requests=[IO.StreamWriter]::new($engine.StandardInput.BaseStream,[Text.UTF8Encoding]::new($false))
    $requests.AutoFlush=$true
    foreach($number in $argsPdf.pages){
        $render=Render-Page $document $number 2350
        try {
            $image=Join-Path $root 'current-page.png'
            $net=[System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($render)
            $picture=[Drawing.Bitmap]::new($net)
            try {$picture.Save($image,[Drawing.Imaging.ImageFormat]::Png)} finally {$picture.Dispose()}
            $requests.WriteLine($image)
            $boxes=$engine.StandardOutput.ReadLine()
            if([string]::IsNullOrEmpty($boxes)){throw ('OCR stopped: '+$errors.GetAwaiter().GetResult())}
            $blank=($boxes -eq '[]' -and (Is-VisuallyBlank $render))
            [Console]::WriteLine(('{"Number":'+[int]$number+',"Blank":'+$blank.ToString().ToLowerInvariant()+',"Boxes":'+$boxes+'}'))
        } finally {$render.Dispose()}
    }
    $requests.Close()
    if(!$engine.WaitForExit(10000)){throw 'OCR process did not exit.'}
    if($engine.ExitCode -ne 0){throw $errors.GetAwaiter().GetResult()}
} catch {
    [Console]::Error.WriteLine($phase + ' ' + $_.ToString())
    exit 1
} finally {
    if($engine){try{if(!$engine.HasExited){$engine.Kill()}}finally{$engine.Dispose()}}
    if($file){$file.Dispose()}
    if($inputFile){$inputFile.Dispose()}
}
