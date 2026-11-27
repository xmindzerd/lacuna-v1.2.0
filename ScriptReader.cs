using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private CancellationTokenSource scriptReaderCancel;

        private void ShowContainingFolder(string file)
        {
            TryAction(delegate
            {
                string folder = Path.GetDirectoryName(file);
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("所属文件夹已移走或无法访问。");
                using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })) { }
            });
        }

        private void ShowScript()
        {
            RenderAnalysis(true,null,null,0,null);
        }

        private void ShowScriptReader(string backLabel, Action back, int line, ScriptDocument expected)
        {
            double offset=Find<ScrollViewer>("WorkspaceScroll").VerticalOffset;
            Action restore=null;
            if(back!=null) restore=delegate {
                back();
                int revision=pageRevision;
                Window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate {
                    if(pageRevision==revision)Find<ScrollViewer>("WorkspaceScroll").ScrollToVerticalOffset(offset);
                }));
            };
            RenderAnalysis(true,backLabel,restore,line,expected);
        }

        private async void DrawScriptReader(int line, ScriptDocument expected, StackPanel host = null)
        {
            host = host ?? content;
            string folder = projectFolder;
            string relative = null;
            string path = null;
            if (!TryAction(delegate
            {
                relative = store.OpenProject(folder).Script;
                if (String.IsNullOrEmpty(relative)) throw new InvalidOperationException("请先导入剧本。");
                path = AgentData.Inside(folder, relative);
            })) return;
            var actions = new WrapPanel();
            host.Children.Add(actions);
            var status = Muted("正在读取剧本…", 14, 8);
            status.TextWrapping = TextWrapping.Wrap;
            host.Children.Add(status);
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            scriptReaderCancel = cancellation;
            int openRevision = pageRevision;
            try
            {
                var source = await Task.Run(delegate
                {
                    if (expected == null && PdfScript.IsPdf(path) && !PdfScript.HasTranscript(path))
                    {
                        using (AnalysisData.Lock(folder))
                        {
                            token.ThrowIfCancellationRequested();
                            if (new ProjectStore(null).OpenProject(folder).Script != relative)
                                throw new InvalidOperationException("当前剧本已经更新，请重新打开查阅。");
                            if (!PdfScript.HasTranscript(path))
                                PdfScript.Prepare(path, progress =>
                                {
                                    Window.Dispatcher.BeginInvoke(new Action(delegate
                                    {
                                        if (scriptReaderCancel == cancellation && !token.IsCancellationRequested && pageRevision == openRevision)
                                            status.Text = "正在整理 PDF 正文 · " + progress;
                                    }));
                                }, token);
                        }
                    }
                    return ScriptDocument.Read(path, relative);
                });
                if (token.IsCancellationRequested || pageRevision != openRevision) return;
                if (expected != null && (expected.Source != source.Source || expected.Hash != source.Hash))
                    throw new InvalidOperationException("当前原件已经更新，请重新打开审核结果。");
                List<IGrouping<string, AnalysisScene>> groups = null;
                try
                {
                    var version = AnalysisData.Load(folder).Current(source);
                    if (version != null)
                    {
                        var result = AnalysisData.ReadFullResult(folder, version);
                        groups = result.Parts.SelectMany(p => p.scenes).OrderBy(s => s.first)
                            .GroupBy(AnalysisData.EpisodeLabel).ToList();
                    }
                }
                catch (Exception) { groups = null; }
                var region = new StackPanel();
                host.Children.Add(region);
                var picker = new ComboBox { MinWidth=150, ItemsSource=new[] { "全剧全文" }.Concat(
                    groups == null ? Enumerable.Empty<string>() : groups.Select(g => g.Key)).ToList(), SelectedIndex=0 };
                NameControl(picker,"按集查阅剧本");
                var prev = ActionButton("‹ 上一集", () => picker.SelectedIndex--, false);
                var next = ActionButton("下一集 ›", () => picker.SelectedIndex++, false);
                actions.Children.Add(Muted("范围",13,5));
                actions.Children.Add(prev); actions.Children.Add(picker); actions.Children.Add(next);
                Action<int> draw = target => {
                    CancelScriptReaderWork();
                    region.Children.Clear();
                    int index = picker.SelectedIndex;
                    prev.IsEnabled = index > 0;
                    next.IsEnabled = index+1 < picker.Items.Count;
                    int[] rows = index <= 0 ? null : groups[index-1].SelectMany(s =>
                        Enumerable.Range(s.first, s.last-s.first+1)).Distinct().OrderBy(n=>n).ToArray();
                    if (rows!=null && rows.Length==0) { region.Children.Add(Notice("本集尚无可查阅的正文。")); return; }
                    var toolbar = new WrapPanel();
                    region.Children.Add(toolbar);
                    new ScriptReaderView(this, source, path, target, rows).Show(toolbar, region);
                };
                picker.SelectionChanged += delegate { draw(0); };
                if (scriptReaderCancel == cancellation) scriptReaderCancel = null;
                draw(line);
                host.Children.Remove(status);
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    status.Text = "无法查阅剧本。 " + AgentData.Short(error.Message, 380);
            }
            finally
            {
                if (scriptReaderCancel == cancellation) scriptReaderCancel = null;
                cancellation.Dispose();
            }
        }

        private void CancelScriptReaderWork()
        {
            if (scriptReaderCancel != null)
            {
                scriptReaderCancel.Cancel();
                scriptReaderCancel = null;
            }
        }

        private sealed class ScriptReaderView
        {
            private readonly Workspace workspace;
            private readonly ScriptDocument source;
            private readonly string path;
            private readonly int[] starts, originalRows;
            private int OriginalRow(int row) { return originalRows == null ? row : originalRows[row-1]; }
            private readonly bool pdf;
            private readonly List<ScriptMatch> matches = new List<ScriptMatch>();
            private TextBox query, body, jump;
            private TextBlock matchStatus, pageStatus, previewStatus;
            private Button previous, next, prevHit, nextHit;
            private ComboBox mode;
            private readonly int[] pdfNumbers;
            private readonly ScriptDocument fullSource;
            private ContentControl pages;
            private ScrollViewer preview;
            private int active, match = -1;
            private bool showingPage;

            internal ScriptReaderView(Workspace workspace, ScriptDocument source, string path, int line, int[] rows = null)
            {
                originalRows = rows;
                this.workspace = workspace;
                fullSource = source;
                this.source = rows == null ? source : new ScriptDocument { Lines = rows.Select(n => source.Lines[n-1]).ToArray() };
                this.path = path;
                pdf = source.PdfPageStarts != null;
                if (pdf)
                {
                    var offsets = new List<int>();
                    var numbers = new List<int>();
                    for (int i = 0; i < source.PdfPageStarts.Length; i++)
                    {
                        int first = source.PdfPageStarts[i];
                        int end = i+1 == source.PdfPageStarts.Length ? source.Lines.Length+1 : source.PdfPageStarts[i+1];
                        int at = rows == null ? first : Array.FindIndex(rows, n => n >= first && n < end)+1;
                        if (at > 0) { offsets.Add(at); numbers.Add(i+1); }
                    }
                    starts = offsets.ToArray(); pdfNumbers = numbers.ToArray();
                }
                else starts = Enumerable.Range(0, Math.Max(1,(this.source.Lines.Length+159)/160)).Select(i=>i*160+1).ToArray();
                active = PageIndex(Math.Max(1, Math.Min(line, source.Lines.Length)));
                StartLine = line;
            }

            private int StartLine { get; set; }

            internal void Show(WrapPanel actions, StackPanel host)
            {
                if (pdf)
                {
                    mode = new ComboBox { ItemsSource=new[] { "仅看文字","文字 + PDF 原页同屏对照","仅看 PDF 原页" }, SelectedIndex=0 };
                    Workspace.NameControl(mode,"PDF 阅读模式");
                    actions.Children.Add(mode);
                    var suspect = pdfNumbers.Where(n => fullSource.PdfPages[n-1].Scanned &&
                        ScriptDocument.SplitLines(fullSource.PdfPages[n-1].Text).Any(PdfScript.SuspectEnglish)).ToArray();
                    if (suspect.Length > 0) {
                        var alerts = new WrapPanel();
                        alerts.Children.Add(workspace.StatusBadge("扫描英文待核对页","Review"));
                        foreach (int n in suspect) alerts.Children.Add(workspace.ActionButton("第 "+n+" 页", delegate {
                            active = Array.IndexOf(pdfNumbers,n); mode.SelectedIndex=1; ShowPage(active);
                        },false));
                        host.Children.Add(alerts);
                    }
                }
                var search = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
                query = Field(originalRows != null ? "搜索本集正文" : "搜索剧本全文", 240);
                search.Children.Add(query);
                search.Children.Add(workspace.ActionButton(originalRows != null ? "搜索本集" : "搜索全文", delegate { Find(1); }, false));
                prevHit = workspace.ActionButton("上一个匹配", delegate { Find(-1); }, false);
                nextHit = workspace.ActionButton("下一个匹配", delegate { Find(1); }, false);
                search.Children.Add(prevHit);
                search.Children.Add(nextHit);
                matchStatus = workspace.Muted(originalRows != null ? "输入关键词，可搜索本集正文。" : "输入关键词，可搜索整个剧本。", 13, 8);
                search.Children.Add(matchStatus);
                query.TextChanged += delegate
                {
                    matches.Clear();
                    match = -1;
                    prevHit.IsEnabled = nextHit.IsEnabled = false;
                    matchStatus.Text = "";
                };
                query.KeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if (args.Key == Key.Enter) { Find(1); args.Handled = true; }
                };
                prevHit.IsEnabled = nextHit.IsEnabled = false;
                host.Children.Add(search);

                var paging = new WrapPanel();
                previous = workspace.ActionButton(pdf ? "上一页" : "上一段", delegate { ShowPage(active - 1); }, false);
                next = workspace.ActionButton(pdf ? "下一页" : "下一段", delegate { ShowPage(active + 1); }, false);
                paging.Children.Add(previous);
                paging.Children.Add(next);
                jump = Field(pdf ? "要跳转的 PDF 页码" : "要跳转的原文行号", 90);
                paging.Children.Add(jump);
                paging.Children.Add(workspace.ActionButton(pdf ? "跳到页码" : "跳到行号", Jump, false));
                jump.KeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if (args.Key == Key.Enter) { Jump(); args.Handled = true; }
                };
                if (pdf)
                {
                    var row = Field("原文行号",90);
                    Action jumpRow = delegate {
                        int n;
                        if (!Int32.TryParse(row.Text,out n)) n=0;
                        int local = originalRows==null ? n : Array.BinarySearch(originalRows,n)+1;
                        if (local<1 || local>source.Lines.Length) { workspace.SetStatus("请输入当前范围内的原文行号。"); return; }
                        Highlight(local,0,source.Lines[local-1].Length);
                    };
                    row.KeyDown += (sender,e)=>{ if(e.Key==Key.Enter) { jumpRow();e.Handled=true; } };
                    paging.Children.Add(row);
                    paging.Children.Add(workspace.ActionButton("跳到原文行",jumpRow,false));
                }
                host.Children.Add(paging);
                pageStatus = workspace.Muted("", 13, 6);
                pageStatus.TextWrapping = TextWrapping.Wrap;
                host.Children.Add(pageStatus);
                body = new TextBox
                {
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 440,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 15, Padding = new Thickness(16, 12, 16, 12)
                };
                AutomationProperties.SetName(body, "剧本正文");
                pages = new ContentControl();
                host.Children.Add(pages);
                pages.Content = body;
                if (pdf)
                {
                    previewStatus = workspace.Muted("", 14, 7);
                    preview = new ScrollViewer { Height = 400, Content = previewStatus, Visibility = Visibility.Collapsed,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    mode.SelectionChanged += delegate {
                        pages.Content = null;
                        var bodyParent = body.Parent as Border;
                        if (bodyParent != null) bodyParent.Child = null;
                        var previewParent = preview.Parent as Border;
                        if (previewParent != null) previewParent.Child = null;
                        showingPage = mode.SelectedIndex != 0;
                        preview.Visibility = Visibility.Visible;
                        pages.Content = mode.SelectedIndex == 0 ? (FrameworkElement)body : mode.SelectedIndex == 2 ?
                            (FrameworkElement)preview : workspace.StudioColumns(body,preview,1,1,620);
                        if (showingPage) RenderPreview(pdfNumbers[active]); else workspace.CancelScriptReaderWork();
                    };
                }
                ShowPage(active);
                if (StartLine >= 1 && StartLine <= source.Lines.Length)
                    Highlight(StartLine, 0, source.Lines[StartLine - 1].Length);
            }

            private TextBox Field(string name, double width)
            {
                var box = new TextBox { Width = width, Height = 34, Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(8, 3, 8, 3), VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(box, name);
                box.ToolTip = name;
                return box;
            }

            private int PageIndex(int line)
            {
                int index = Array.BinarySearch(starts, line);
                return index >= 0 ? index : ~index - 1;
            }

            private void Jump()
            {
                int number;
                if (!Int32.TryParse(jump.Text, out number)) number = 0;
                if (pdf) number = Array.IndexOf(pdfNumbers,number)+1;
                else if (originalRows != null) number = Array.BinarySearch(originalRows, number) + 1;
                if (number <= 0 || number > (pdf ? starts.Length : source.Lines.Length))
                {
                    workspace.SetStatus(pdf ? "请输入有效的 PDF 页码。"
                        : originalRows != null && originalRows.Length > 0
                            ? "请输入本集原文行号（" + originalRows[0] + "–" + originalRows[originalRows.Length - 1] + "）。"
                            : "请输入有效的原文行号。");
                    return;
                }
                workspace.SetStatus(null);
                if (pdf) ShowPage(number - 1);
                else Highlight(number, 0, source.Lines[number - 1].Length);
            }

            private int RowNumberWidth(int pageIndex)
            {
                int end = pageIndex + 1 == starts.Length ? source.Lines.Length : starts[pageIndex + 1] - 1;
                return Math.Max(3, OriginalRow(end).ToString().Length);
            }

            private void ShowPage(int index)
            {
                active = index;
                previous.IsEnabled = index > 0;
                next.IsEnabled = index + 1 < starts.Length;
                int first = starts[index];
                int end = index + 1 == starts.Length ? source.Lines.Length : starts[index + 1] - 1;
                string tag = "";
                if (pdf)
                {
                    var page = fullSource.PdfPages[pdfNumbers[index]-1];
                    tag = page.Blank ? " · 空白页" : page.Scanned ? " · 扫描识别，请与原页核对" : " · PDF 文字层";
                }
                pageStatus.Text = (pdf ? "PDF 第 " : originalRows != null ? "本集第 " : "第 ") + (pdf ? pdfNumbers[index] : index + 1) + " / " + (pdf ? fullSource.PdfPages.Length : starts.Length) + (pdf ? " 页" : " 段")
                    + tag + " · 原文第 " + OriginalRow(first) + (first == end ? "" : "–" + OriginalRow(end)) + " 行";
                int width = RowNumberWidth(index);
                body.Text = String.Join("\n", source.Lines.Skip(first - 1).Take(end - first + 1)
                    .Select((text, i) => OriginalRow(i + first).ToString().PadLeft(width) + "  " + text));
                body.ScrollToHome();
                if (showingPage) RenderPreview(pdfNumbers[index]);
            }

            private void Find(int direction)
            {
                string keyword = query.Text.Trim();
                if (keyword.Length == 0) { matchStatus.Text = "请先输入关键词。"; return; }
                if (match < 0)
                {
                    matches.Clear();
                    for (int i = 0; i < source.Lines.Length; i++)
                    {
                        string line = source.Lines[i];
                        for (int cursor = 0; cursor < line.Length;)
                        {
                            int offset = line.IndexOf(keyword, cursor, StringComparison.OrdinalIgnoreCase);
                            if (offset < 0) break;
                            matches.Add(new ScriptMatch { Line = i + 1, Offset = offset });
                            cursor = offset + keyword.Length;
                        }
                    }
                    if (matches.Count == 0) { matchStatus.Text = "全文没有匹配。"; return; }
                    match = direction > 0 ? 0 : matches.Count - 1;
                }
                else match = (match + direction + matches.Count) % matches.Count;
                prevHit.IsEnabled = nextHit.IsEnabled = matches.Count > 1;
                var hit = matches[match];
                matchStatus.Text = "匹配 " + (match + 1) + " / " + matches.Count + " · " + fullSource.LineLocation(OriginalRow(hit.Line), OriginalRow(hit.Line));
                Highlight(hit.Line, hit.Offset, keyword.Length);
            }

            private void Highlight(int line, int offset, int length)
            {
                int index = PageIndex(line);
                if (pdf && mode.SelectedIndex == 2) mode.SelectedIndex = 1;
                if (index != active) ShowPage(index);
                int width = RowNumberWidth(index);
                int character = width + 2 + offset;
                for (int i = starts[index]; i < line; i++) character += width + 3 + source.Lines[i - 1].Length;
                body.UpdateLayout();
                body.Select(character, length);
                body.Focus();
                int visualLine = body.GetLineIndexFromCharacterIndex(character);
                if (visualLine >= 0) body.ScrollToLine(Math.Max(0, visualLine - 2));
            }

            private async void RenderPreview(int page)
            {
                workspace.CancelScriptReaderWork();
                var cancel = new CancellationTokenSource();
                workspace.scriptReaderCancel = cancel;
                previewStatus.Text = "正在打开 PDF 第 " + page + " 页…";
                preview.Content = previewStatus;
                try
                {
                    byte[] bytes = await Task.Run(() => PdfScript.PageImage(path, page, cancel.Token));
                    if (cancel.IsCancellationRequested) return;
                    using (var stream = new MemoryStream(bytes))
                    {
                        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var image = new Image { Source = bitmap, MaxWidth = 920 };
                        AutomationProperties.SetName(image, "PDF 第 " + page + " 页原页");
                        preview.Content = image;
                        preview.ScrollToTop();
                    }
                }
                catch (Exception e)
                {
                    if (!cancel.IsCancellationRequested) previewStatus.Text = AgentData.Short(e.Message, 350);
                }
                finally
                {
                    if (workspace.scriptReaderCancel == cancel) workspace.scriptReaderCancel = null;
                    cancel.Dispose();
                }
            }
        }

        private struct ScriptMatch
        {
            internal int Line, Offset;
        }
    }
}
