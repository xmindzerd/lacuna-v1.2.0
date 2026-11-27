using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace OneClickFilm
{
    public sealed class ScriptPdfPage
    {
        public int Number { get; set; }
        public string Text { get; set; }
        public bool Blank { get; set; }
        public bool Scanned { get; set; }
        public string Language { get; set; }
    }

    public sealed class ScriptPdfTranscript
    {
        public int Format { get; set; }
        public string OriginalHash { get; set; }
        public List<ScriptPdfPage> Pages { get; set; }
    }

    internal static class PdfScript
    {
        internal static bool SuspectEnglish(string line)
        {
            return !String.IsNullOrEmpty(line) && Regex.IsMatch(line, @"(?<![A-Za-z])(?:[A-Za-z]\s+){2}[A-Za-z](?![A-Za-z])|(?<![A-Za-z])[A-Z][a-z]\s+[a-z]{1,2}(?=\s+[A-Z])|[a-z]{2}[A-Z][a-z]{2}|\b[A-Z]{2}[a-z]\b|(?<!\S)\|(?!\S)");
        }
        private const int MaxPages = 1000;
        private const long MaxPdfBytes = 128L * 1024 * 1024;
        private const int MaxTranscriptBytes = 16 * 1024 * 1024;

        private static readonly Dictionary<string,Assembly> Libraries=new Dictionary<string,Assembly>(StringComparer.OrdinalIgnoreCase);

        internal static void RegisterLibraries()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                var name = new AssemblyName(args.Name).Name;
                if (String.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                lock(Libraries)
                {
                    Assembly loaded;
                    if(Libraries.TryGetValue(args.Name,out loaded))return loaded;
                    var host = typeof(PdfScript).Assembly;
                    using (var assembly = host.GetManifestResourceStream("PdfLibraries." + name + ".dll"))
                    {
                        if (assembly == null) return null;
                        using (var bytes = new MemoryStream())
                        {
                            assembly.CopyTo(bytes);
                            loaded=Assembly.Load(bytes.ToArray());
                            Libraries[args.Name]=loaded;
                            return loaded;
                        }
                    }
                }
            };
        }

        internal static bool IsPdf(string path)
        {
            return String.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasTranscript(string path)
        {
            return File.Exists(TranscriptPath(path));
        }

        internal static void Prepare(string path, Action<string> progress, CancellationToken cancel)
        {
            string originalHash = OriginalHash(path);
            var transcript = Extract(path, progress, cancel);
            transcript.OriginalHash = originalHash;
            MakeSource(transcript, null, "");
            cancel.ThrowIfCancellationRequested();
            if (OriginalHash(path) != originalHash)
                throw new InvalidDataException("读取期间 PDF 文件发生变化，请重新导入。");
            cancel.ThrowIfCancellationRequested();
            AgentData.Save(TranscriptPath(path), transcript);
        }

        internal static ScriptDocument Read(string path, string source)
        {
            string transcriptPath = TranscriptPath(path);
            if (!File.Exists(transcriptPath))
                throw new InvalidDataException("找不到 PDF 剧本正文，请从完好的项目副本找回正文，或重新导入 PDF。");
            byte[] raw;
            using (var stream = File.OpenRead(transcriptPath))
            {
                if (stream.Length > MaxTranscriptBytes) throw new InvalidDataException("PDF 剧本正文记录超过大小上限。");
                using (var copy = new MemoryStream()) { stream.CopyTo(copy); raw = copy.ToArray(); }
            }
            ScriptPdfTranscript transcript;
            const string invalidTranscript = "PDF 剧本正文记录无法读取。请从完好的项目副本恢复，或重新导入 PDF。";
            try
            {
                transcript = AgentData.Json().Deserialize<ScriptPdfTranscript>(Encoding.UTF8.GetString(raw));
            }
            catch (ArgumentException error) { throw new InvalidDataException(invalidTranscript, error); }
            catch (InvalidOperationException error) { throw new InvalidDataException(invalidTranscript, error); }
            if (transcript == null) throw new InvalidDataException(invalidTranscript);
            string original = OriginalHash(path);
            if (transcript.OriginalHash != original)
                throw new InvalidDataException("PDF 原件与项目正文记录不一致。请在项目中重新导入 PDF。");
            string identity = AgentData.Hash(original + "\n" + AnalysisData.Digest(raw));
            return MakeSource(transcript, source, identity);
        }

        private static ScriptDocument MakeSource(ScriptPdfTranscript transcript, string source, string identity)
        {
            if (transcript == null || transcript.Format != 1 || transcript.Pages == null ||
                transcript.Pages.Count < 1 || transcript.Pages.Count > MaxPages)
                throw new InvalidDataException("PDF 剧本正文记录不完整。请重新导入原件。");
            var lines = new List<string>();
            var starts = new List<int>();
            for (int index = 0; index < transcript.Pages.Count; index++)
            {
                var page = transcript.Pages[index];
                if (page == null || page.Number != index + 1 || page.Text == null ||
                    (String.IsNullOrWhiteSpace(page.Text) && !page.Blank))
                    throw new InvalidDataException("PDF 第 " + (index + 1) + " 页没有可用正文。请核对页面。");
                if (!page.Scanned) page.Text = page.Text.Replace('\u0001', ' ');
                starts.Add(lines.Count + 1);
                lines.AddRange(ScriptDocument.SplitLines(page.Text));
            }
            ScriptDocument.ValidateLines(lines);
            return new ScriptDocument { Source = source, Hash = identity, Lines = lines.ToArray(),
                PdfPageStarts = starts.ToArray(), PdfPages = transcript.Pages.ToArray() };
        }

        private static ScriptPdfTranscript Extract(string path, Action<string> progress, CancellationToken cancel)
        {
            var transcript = new ScriptPdfTranscript { Format = 1, Pages = new List<ScriptPdfPage>() };
            var readImages = new List<int>();
            try
            {
                using (var document = PdfDocument.Open(path))
                {
                    if (document.NumberOfPages < 1 || document.NumberOfPages > MaxPages)
                        throw new InvalidDataException("每份 PDF 剧本支持 1 到 " + MaxPages + " 页。请分集导入。");
                    for (int n = 1; n <= document.NumberOfPages; n++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var readClock=FlowCreation.Development?System.Diagnostics.Stopwatch.StartNew():null;
                        var page = document.GetPage(n);
                        string text = ContentOrderTextExtractor.GetText(page).Replace('\u0001', ' ').Trim();
                        if(readClock!=null)DevMonitor.Log(null,"MEDIA/OCR","PdfPig 第 "+n+" 页 · "+text.Length+" 字 · "+readClock.ElapsedMilliseconds+" ms");
                        bool hasReadableText = text.Any(Char.IsLetter) && !text.Contains("\uFFFD");
                        if (!hasReadableText || (text.Length < 80 && page.GetImages().Any(image =>
                            image.BoundingBox.Width >= page.Width * .55 && image.BoundingBox.Height >= page.Height * .45)))
                            readImages.Add(n);
                        transcript.Pages.Add(new ScriptPdfPage { Number = n, Text = text });
                        if (progress != null && (n == document.NumberOfPages || n % 8 == 0))
                            progress("已检查 PDF " + n + " / " + document.NumberOfPages + " 页");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception e)
            {
                string type = e.GetType().Name;
                if (type.IndexOf("Encrypted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("这份 PDF 有密码保护。请先保存为可直接打开的 PDF 再导入。", e);
                throw new InvalidDataException("无法解析这份 PDF。请检查文件是否损坏，或在 PDF 阅读器另存后重试。", e);
            }
            if (readImages.Count == 0) return transcript;

            if (progress != null) progress("正在识别 " + readImages.Count + " 张扫描页…");
            var recognized = new Dictionary<int, ScriptPdfPage>();
            string ocrRoot = Path.Combine(Path.GetTempPath(),"Lacuna-RapidOCR-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ocrRoot);
            try {
                using(var stream=typeof(PdfScript).Assembly.GetManifestResourceStream("RapidOCR.zip"))
                using(var zip=new System.IO.Compression.ZipArchive(stream,System.IO.Compression.ZipArchiveMode.Read))
                    foreach(var entry in zip.Entries) {
                        if(entry.Name.Length==0)continue;
                        string file=AgentData.Inside(ocrRoot,entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        using(var input=entry.Open()) using(var output=File.Create(file))input.CopyTo(output);
                    }
                var ocrClock=FlowCreation.Development?System.Diagnostics.Stopwatch.StartNew():null;
                RunWindows(new { mode = "ocr", file = Path.GetFullPath(path), pages = readImages, ocrRoot }, 900000, line =>
            {
                var page = RapidLayout.ReadPage(line);
                if (page == null || !readImages.Contains(page.Number) || recognized.ContainsKey(page.Number))
                    throw new InvalidDataException("扫描识别返回了无效的 PDF 页码。");
                recognized.Add(page.Number, page);
                if(ocrClock!=null){DevMonitor.Log(null,"MEDIA/OCR","RapidOCR 第 "+page.Number+" 页 · "+(page.Text??"").Length+" 字 · 距前页 "+ocrClock.ElapsedMilliseconds+" ms");ocrClock.Restart();}
                if (progress != null) progress("已识别 " + recognized.Count + " / " + readImages.Count + " 张扫描页");
                }, cancel);
            } finally {
                try { Directory.Delete(ocrRoot,true); } catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            foreach (int n in readImages)
            {
                ScriptPdfPage page;
                if (!recognized.TryGetValue(n, out page) || String.IsNullOrWhiteSpace(page.Text) && !page.Blank)
                    throw new InvalidDataException("PDF 第 " + n + " 页未能识别出正文。请核查页面或用清晰版本重新导入。");
                page.Scanned = !page.Blank;
                transcript.Pages[n - 1] = page;
            }
            return transcript;
        }

        internal static byte[] PageImage(string path, int page, CancellationToken cancel)
        {
            return Convert.FromBase64String(RunWindows(new { mode = "preview", file = Path.GetFullPath(path), page = page },
                30000, null, cancel));
        }

        private static string RunWindows(object request, int timeout, Action<string> progress, CancellationToken cancel)
        {
            string exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(exe)) throw new InvalidOperationException("这台 Windows 无法使用内置 PDF 原页和扫描识别组件。");
            string tempScript = Path.Combine(Path.GetTempPath(), "Lacuna-PdfWin-" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                using (var script = new StreamReader(typeof(PdfScript).Assembly.GetManifestResourceStream("PdfWindows.ps1")))
                    File.WriteAllText(tempScript, script.ReadToEnd(), new UTF8Encoding(true));
                var result = CodexConnection.Run(exe, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + AgentRunner.Quote(tempScript), timeout,
                    input: AgentData.Json().Serialize(request), onLine: progress, cancellation: cancel);
                if (result.Code != 0)
                {
                    throw new InvalidOperationException("Windows 读取 PDF 原页失败。 " + AgentData.Short(result.Error, 280));
                }
                return result.Output.Trim();
            }
            finally
            {
                try { if (File.Exists(tempScript)) File.Delete(tempScript); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string TranscriptPath(string pdf)
        {
            return AgentData.Inside(Path.GetDirectoryName(pdf), Path.GetFileName(pdf) + ".正文.json");
        }

        private static string OriginalHash(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > MaxPdfBytes) throw new InvalidDataException("PDF 超过 128 MB。请按集拆分后导入。");
                return SystemData.Digest(stream);
            }
        }
    }
}
