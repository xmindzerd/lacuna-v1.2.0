param(
    [Alias("OutDir")][string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Building requires the Windows .NET Framework 4.x compiler.'
}

$destination = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($destination)
$executable = Join-Path $destination 'Lacuna.exe'
$sources = @('BuiltinSkills.cs','DirectorPipeline.cs','VideoPromptFidelity.cs','ProductionRules.cs','NovelAuth.cs','NovelVideo.cs','VideoChoices.cs','VideoDesk.cs','VideoPlatform.cs', 'VideoDelivery.cs', 'VideoViews.cs', 'FlowSkills.cs', 'FlowCreation.cs', 'FlowTelemetry.cs', 'FlowIncident.cs', 'FlowAutoRepair.cs', 'FlowProduction.cs', 'FlowViews.cs', 'FlowOverviewUi.cs', 'FlowChannels.cs', 'Program.cs', 'ProjectStore.cs', 'Stage.cs', 'Workspace.cs', 'WorkbenchPages.cs', 'ProjectAnalysis.cs', 'CodexAuthMode.cs', 'OpenAiResponsesClient.cs', 'ImageApiProfiles.cs', 'AssetUsageLedger.cs', 'AnalysisKeyStore.cs', 'ProjectAnalysisViews.cs', 'PdfScript.cs','RapidLayout.cs', 'ScriptReader.cs', 'CodexConnection.cs', 'TaskProcessGroup.cs', 'CodexSettings.cs', 'CodexAppServer.cs','CodexServerTurn.cs','CodexServerAccount.cs','AgentCore.cs', 'AgentMcp.cs', 'AgentMedia.cs', 'ImageRunTrace.cs', 'NativeImageRetry.cs', 'AgentUi.cs', 'AgentControls.cs', 'SystemData.cs', 'SystemPackages.cs', 'SystemViews.cs', 'LocalSkills.cs', 'SkillViews.cs', 'AssetProduction.cs', 'AssetTasks.cs', 'AssetChannels.cs', 'AssetViews.cs', 'AssetWorkspaceUi.cs', 'AssetPrompting.cs', 'AssetImageBatches.cs', 'AssetImageRunner.cs', 'ImageOutputSettings.cs', 'SkillCompatibility.cs', 'Film.cs', 'FilmWriter.cs', 'FilmViews.cs', 'ProjectReview.cs', 'UiFx.cs') | ForEach-Object {
    Join-Path $PSScriptRoot $_
}
$sources += Join-Path $PSScriptRoot 'ProjectNavigation.cs'
$sources += Join-Path $PSScriptRoot 'MonitorDesk.cs'
$sources += Join-Path $PSScriptRoot 'PromptMonitor.cs'
$sources += Join-Path $PSScriptRoot 'StudioLayout.cs'
$sources += 'ApiTestDialog.cs','DevMonitor.cs','Feedback.cs','FeedbackViews.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$references = @(
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll',
    'System.Xaml.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll',
    'System.IO.Compression.dll', 'System.Security.dll', 'Microsoft.VisualBasic.dll'
) | ForEach-Object {
    '/reference:' + (Join-Path $framework $_)
}

$resources = @('VideoModels.json','Workspace.xaml', 'WorkspaceView.xaml', 'UiTheme.xaml', 'BrandMark.png', 'BrandWordmark.png', 'Brand.ico', 'Workbench.png', 'PdfWindows.ps1', '第三方组件许可.txt') | ForEach-Object {
    '/resource:' + (Join-Path $PSScriptRoot $_) + ',' + $_
}
$resources += '/resource:' + (Join-Path $PSScriptRoot 'lacuna-feedback-diagnostics/SKILL.md') + ',FeedbackSkill'
$pdfLibraries = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'third-party/PDF') -Filter '*.dll' -File)
$references += $pdfLibraries | ForEach-Object { '/reference:' + $_.FullName }
$resources += $pdfLibraries | ForEach-Object { '/resource:' + $_.FullName + ',PdfLibraries.' + $_.Name }
$ocrZip = Join-Path $destination ('Lacuna-RapidOCR-'+[Guid]::NewGuid().ToString('N')+'.zip')
$skillsZip = Join-Path $destination ('Lacuna-BuiltinSkills-'+[Guid]::NewGuid().ToString('N')+'.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
$ocrFolder=Join-Path $PSScriptRoot 'third-party/RapidOCR'
foreach($asset in 'Lacuna.Ocr.exe','OpenCvSharpExtern.dll','onnxruntime.dll','ppocr_keys_v1.txt','ch_PP-OCRv4_det_infer.onnx','ch_PP-OCRv4_rec_infer.onnx','ch_ppocr_mobile_v2.0_cls_infer.onnx','licenses/RapidOCR-LICENSE.txt','licenses/PaddleOCR-LICENSE.txt','licenses/ONNXRuntime-1.32-LICENSE.txt'){
    if(!(Test-Path -LiteralPath (Join-Path $ocrFolder $asset) -PathType Leaf)){throw ("Missing packaged OCR asset: "+$asset)}
}
$skillsFolder=Join-Path $PSScriptRoot 'builtin-skills'
foreach($skillAsset in 'manifest.json','xiaoyu-director-script-analysis/SKILL.md','short-drama-assets-zenstory/SKILL.md','lacuna-asset-image-rules/SKILL.md','xiaoyu-vertical-shotlist/SKILL.md','xiaoyu-seedance-one-click-prompts/SKILL.md'){
    if(!(Test-Path -LiteralPath (Join-Path $skillsFolder $skillAsset) -PathType Leaf)){throw ("Missing packaged built-in skill asset: "+$skillAsset)}
}
[IO.Compression.ZipFile]::CreateFromDirectory($ocrFolder,$ocrZip)
[IO.Compression.ZipFile]::CreateFromDirectory($skillsFolder,$skillsZip)
$resources += '/resource:'+$ocrZip+',RapidOCR.zip'
$resources += '/resource:'+$skillsZip+',BuiltinSkills.zip'
& $compiler /nologo /langversion:5 /warnaserror /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 `
    ("/out:" + $executable) ("/win32icon:" + (Join-Path $PSScriptRoot 'Brand.ico')) @resources `
    @references @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
} finally {
    if ([IO.File]::Exists($ocrZip)) { [IO.File]::Delete($ocrZip) }
    if ([IO.File]::Exists($skillsZip)) { [IO.File]::Delete($skillsZip) }
}
Get-Item -LiteralPath $executable




