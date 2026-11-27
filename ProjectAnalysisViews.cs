using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private TextBlock analysisProgress;

        private string reviewEpisode;
        private Action<bool,int,ScriptDocument> switchAnalysisTab;
        private bool readingOriginal;

        private void ShowProjectAnalysis() { RenderAnalysis(false, null, null, 0, null); }

        private void RenderAnalysis(bool original, string backLabel, Action back, int line, ScriptDocument expected)
        {
            string root = projectFolder;
            BeginPage(1, "剧本导入与分析", DisplayName(), 1);
            switchAnalysisTab=null;
            readingOriginal=original;
            if (back != null) AddPageBack(backLabel, back);
            if (root == null) return;
            var task = assetTasks.FirstOrDefault(t => SameProject(root, t.Root) && t.Name == "剧本分析");
            bool available = ScriptAvailable();
            var import = ActionButton(available ? "更换剧本" : "导入剧本", ImportScript, !available);
            import.IsEnabled = task == null && scriptImportCancel == null;
            var secondary = new WrapPanel();
            if (scriptImportCancel != null && SameProject(root,scriptImportProject))
                secondary.Children.Add(ActionButton("取消导入", () => UiFx.SafeCancel(scriptImportCancel), false));
            if (!available)
            {
                StageStatus(1,"尚未导入");
                content.Children.Add(ActionHeader("尚未导入", import));
                content.Children.Add(secondary);
                content.Children.Add(SkillPicker(root,LocalSkills.StageKey(0),"剧本分析"));
                content.Children.Add(Notice("可导入 PDF、TXT、Markdown 或 DOCX。"));
                return;
            }
            string path = store.ProjectFile(root, project.Script);
            ScriptDocument source;
            AnalysisLedger ledger;
            if (!ReadProjectAnalysis(out source, out ledger) || source == null)
            {
                content.Children.Add(import);
                DrawScriptReader(line, expected);
                return;
            }
            var latest = ledger.Current(source);
            bool pending = latest != null && latest != ledger.Confirmed(source);
            var result = latest == null ? null : ReadFullAnalysisResult(latest);
            Action refresh = () => RenderAnalysis(readingOriginal,backLabel,back,readingOriginal ? line : 0,readingOriginal ? expected : null);
            Action confirm = delegate {
                string unused;
                if (Prompt("确认剧本分析","将第 " + latest.Number + " 版清单交给资产阶段。","确认并交接","继续审核",false,out unused)
                    && TryAction(() => AnalysisData.Confirm(root, source, latest.Id))) ShowStage(1);
            };
            string confirmation = "确认这版并交给资产阶段 ›";
            var analyze = ActionButton((CodexAuthMode.IsApi ? "用 Responses API " : "用 Codex（账号）") + (latest == null ? "分析全文" : "重新分析全文"), () => RunProjectAnalysis(root,source), latest == null);
            analyze.IsEnabled = task == null && scriptImportCancel == null;
            var primary = task != null ? ActionButton("停止分析", () => UiFx.SafeCancel(task.Cancel), true) :
                latest == null ? analyze : pending ? ActionButton(confirmation,confirm,true) :
                ActionButton("进入资产制作 ›", () => ShowStage(1), true);
            string state = task != null ? "分析中" : latest == null ? "等待分析" : "第 " + latest.Number + " 版" + (pending ? "待审核" : "已确认");
            StageStatus(1,state);
            var heading = new StackPanel { Orientation=Orientation.Horizontal };
            heading.Children.Add(StatusBadge(state,pending ? "Review" : "Accent"));
            heading.Children.Add(StudioText("  "+Path.GetFileName(path),14,3));
            var head = new DockPanel();
            AddRight(head,primary); head.Children.Add(heading);
            content.Children.Add(head);
            if (latest != null && task == null) secondary.Children.Add(analyze);
            secondary.Children.Add(import);
            if (pending) secondary.Children.Add(ReviewAllButton(ReviewScope.Analysis,refresh));
            string operation = LocalSkills.StageKey(0);
            var repairSkills = ActionButton("检查组合冲突", () => RepairSkills(root, operation, "剧本分析"), false);
            Action<SkillReference> showRepair = choice => repairSkills.Visibility =
                choice != null && choice.Members != null && choice.Members.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            showRepair(null);
            if (!FlowSkills.Managed(root, operation)) TryAction(() => showRepair(LocalSkills.Choice(root, operation)));
            secondary.Children.Add(SkillPicker(root,operation,"剧本分析",showRepair));
            content.Children.Add(secondary);
            var files = new WrapPanel();
            files.Children.Add(repairSkills);
            files.Children.Add(ActionButton("打开原文件", () => TryAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })), false));
            files.Children.Add(ActionButton("打开所属文件夹", () => ShowContainingFolder(path), false));
            string logs = AgentData.Inside(root, @"剧本分析\运行记录");
            if (Directory.Exists(logs)) files.Children.Add(ActionButton("查看分析运行记录", () => TryAction(() => OpenSystemFolder(logs)),false));
            content.Children.Add(new Expander { Header = "更多工具与运行记录", Content = files });
            if (task != null)
            {
                analysisProgress = ProjectAnalysisParagraph(task.TimedStatus,14);
                content.Children.Add(analysisProgress);
                task.Bar = new ProgressBar { Height=6, Maximum=Math.Max(1,task.Total), Value=task.Done, IsIndeterminate=task.Total==0 };
                NameControl(task.Bar,"剧本分析进度");
                content.Children.Add(task.Bar);
            }
            else if (ledger.LastRun != null && ledger.LastRun.Matches(source) && ledger.LastRun.State != "已完成")
                content.Children.Add(Notice(ledger.LastRun.State + " · " + ledger.LastRun.Detail));
            var tabs = new WrapPanel { Margin=new Thickness(0,12,0,10) };
            var body=new StackPanel();
            var analysisTab=ViewTab("分析结果与资产清单",!original,()=>switchAnalysisTab(false,0,null));
            var scriptTab=ViewTab("剧本原文与原页对照",original,()=>switchAnalysisTab(true,0,source));
            tabs.Children.Add(analysisTab);
            tabs.Children.Add(scriptTab);
            content.Children.Add(tabs);
            content.Children.Add(body);
            if (pending && result != null)
                SetFooter("剧本分析第 " + latest.Number + " 版待确认 · 全剧 " + result.Parts.Sum(p => p.scenes.Count) +
                    " 场 · " + result.Assets.Count + " 项待制作资产", ReviewAllButton(ReviewScope.Analysis,refresh),ActionButton(confirmation,confirm,true));
            switchAnalysisTab = (showText,target,document) => {
                CancelScriptReaderWork();
                readingOriginal=showText;
                body.Children.Clear();
                analysisTab.Tag=showText ? null : "Active";
                scriptTab.Tag=showText ? "Active" : null;
                if (showText) DrawScriptReader(target,document,body);
                else if (result!=null)
                {
                    body.Children.Add(StudioText(result.Parts.Sum(p=>p.scenes.Count)+" 场 · 已覆盖 "+result.CoveredLines+
                        " 行 · "+String.Join("、",AnalysisData.Categories.Select(kind=>kind+" "+result.Assets.Count(a=>a.category==kind)))+
                        (latest.Skill==null ? "" : " · 本次 Skill "+latest.Skill.Name),14,4));
                    DrawEpisodeReview(source,result,body);
                }
                else body.Children.Add(Notice(task==null ? "开始分析全文后，可在这里审核分场剧情和待制作资产。" : "分析完成后将展示分场与资产清单。"));
            };
            switchAnalysisTab(original,line,expected);
        }

        private bool ReadProjectAnalysis(out ScriptDocument source, out AnalysisLedger ledger)
        {
            source = null;
            ledger = null;
            try
            {
                source = AnalysisData.CurrentScript(projectFolder);
                ledger = AnalysisData.Load(projectFolder);
                return true;
            }
            catch (Exception e)
            {
                content.Children.Add(Notice("无法读取剧本或分析记录。 " + AgentData.Short(e.Message, 350)));
                return false;
            }
        }

        private async void RunProjectAnalysis(string root, ScriptDocument source)
        {
            if (AssetRunning(root, "剧本分析") || exitAfterCodexTask || systemBusy || checkingCodex || installCancel != null)
            {
                SetStatus("当前有操作进行中，请结束后再开始分析。");
                return;
            }
            if (codexStatus == null || codexStatus.State != CodexState.SignedIn || (!CodexAuthMode.IsApi && codexStatus.Path == null))
            {
                SetStatus(CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先在设置中确认本机 Codex 已登录。");
                RefreshCodex();
                return;
            }
            SelectedSkill skill;
            if (!ResolveSkill(root, LocalSkills.StageKey(0), out skill)) return;
            string provider = CodexAuthMode.Label;
            string explanation = provider + " 将读取项目剧本全文，生成待你审核的场次和资产清单。\n" + CodexAppServer.Summary(agentOptions);
            string answer;
            if (!Prompt("用 " + provider + " 分析全文", explanation
                + (skill == null ? "" : "\n本次 Skill · " + skill.Reference.Name),
                "开始分析", "取消", false, out answer)) return;
            ProjectTaskLease gate = null;
            if (!TryAction(delegate { gate = new ProjectTaskLease(root, "剧本分析"); })) return;
            var task = new AssetTask { Root = root, Name = "剧本分析", Status = provider + " 正在读取剧本全文…" };
            CancellationToken token = task.Cancel.Token;
            AnalysisLedger ledger = null;
            try
            {
                BeginAssetTask(task);
                ledger = AnalysisData.Load(root);
                string model = AgentData.Options().Model;
                string exe = codexStatus.Path;
                ledger.LastRun = new AnalysisRun { State = "分析中", Source = source.Source, SourceHash = source.Hash,
                    Utc = AnalysisData.Now(), Skill = skill == null ? null : skill.Reference };
                AnalysisData.RecordRun(root, ledger.LastRun);
                ShowProjectAnalysis();
                UpdateCodexDisplay();
                Action<int,int,string> report = (done,total,message) => Window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (token.IsCancellationRequested || !assetTasks.Contains(task)) return;
                    task.UpdateAnalysis(done,total,message);
                    if (analysisProgress != null && SameProject(root, projectFolder))
                    {
                        analysisProgress.Text = task.TimedStatus;
                        if (task.Bar != null)
                        {
                            task.Bar.Maximum = Math.Max(1,task.Total);
                            task.Bar.Value = task.Done;
                            task.Bar.IsIndeterminate = task.Total == 0;
                        }
                    }
                    RefreshStatus();
                }));
                var result = await Task.Run(() => AnalysisData.Run(exe, root, source, model,
                    (done,total) => report(done,total,null), token, skill, resumeParts: true, detail: msg => report(0,0,msg)));
                token.ThrowIfCancellationRequested();
                AnalysisData.Publish(root, source, ledger, result);
                AnalysisData.ClearCheckpoint(root);
                if (SameProject(root, projectFolder)) SetStatus(provider + " 已分析全文。可在分析结果与资产清单中审核。");
            }
            catch (Exception e)
            {
                string state = token.IsCancellationRequested ? "分析已停止" : "分析未完成";
                string detail = token.IsCancellationRequested ? "再次分析时将续做尚未完成的段落。" : AgentData.Short(e.Message, 450);
                bool saved = TryAction(delegate
                {
                    if (ledger == null || ledger.LastRun == null) return;
                    AnalysisData.RecordInterruption(root, source, skill, state, detail);
                });
                if (saved && SameProject(root, projectFolder)) SetStatus(state + " · " + detail);
            }
            finally
            {
                bool refresh = analysisProgress != null && SameProject(root, projectFolder);
                gate.Dispose();
                EndAssetTask(task);
                task.Cancel.Dispose();
                if (refresh) ShowProjectAnalysis();
                if (agentPanel != null) RebuildAgentPanel();
                UpdateCodexDisplay();
                if (exitAfterCodexTask)
                {
                    exitAfterCodexTask = false;
                    FinishExit();
                }
            }
        }

        private void ShowAnalysisReview() { ShowEpisodeReview(null); }

        private void ShowEpisodeReview(string selectedEpisode)
        {
            reviewEpisode = selectedEpisode;
            ShowProjectAnalysis();
        }

        private void DrawEpisodeReview(ScriptDocument source, AnalysisResult result, StackPanel host)
        {
            var fullResult = result;
            var episodes = result.Parts.SelectMany(p => p.scenes).Select(AnalysisData.EpisodeLabel).Distinct().ToList();
            episodes.Insert(0, "全部集数");
            string currentEpisode = episodes.Contains(reviewEpisode) ? reviewEpisode : episodes[0];
            Action<string> select = ep => { reviewEpisode = ep; switchAnalysisTab(false,0,null); };
            Action returnToReview = () => select(currentEpisode);
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (episodes.Count > 1)
            {
                int epIndex = episodes.IndexOf(currentEpisode);
                var prevEp = ActionButton("‹ 上一集", () => select(episodes[epIndex - 1]), false);
                prevEp.IsEnabled = epIndex > 0;
                bar.Children.Add(prevEp);
                var picker = new ComboBox { ItemsSource = episodes, SelectedItem = currentEpisode,
                    MinWidth = 156, Margin = new Thickness(0, 0, 8, 6) };
                NameControl(picker, "查看分集分析");
                picker.SelectionChanged += delegate {
                    string next = (string)picker.SelectedItem;
                    if (next != null && next != currentEpisode) select(next);
                };
                bar.Children.Add(picker);
                var nextEp = ActionButton("下一集 ›", () => select(episodes[epIndex + 1]), false);
                nextEp.IsEnabled = epIndex + 1 < episodes.Count;
                bar.Children.Add(nextEp);
                if (epIndex == 0) result = fullResult;
                else if (AnalysisData.Episodes(fullResult).Contains(currentEpisode))
                    result = AnalysisData.SelectEpisodes(fullResult, new [] { currentEpisode });
                else
                {
                    var filteredParts = fullResult.Parts.Select(p => new AnalysisPart {
                        scenes = p.scenes.Where(s => AnalysisData.EpisodeLabel(s) == currentEpisode).ToList(),
                        assets = new System.Collections.Generic.List<AnalysisAsset>(),
                        summary = p.scenes.All(s => AnalysisData.EpisodeLabel(s) == currentEpisode) ? p.summary : "",
                        skillOutput = p.scenes.All(s => AnalysisData.EpisodeLabel(s) == currentEpisode) ? p.skillOutput : ""
                    }).Where(p => p.scenes.Count > 0).ToList();
                    result = new AnalysisResult { Parts = filteredParts, Assets = new System.Collections.Generic.List<AnalysisAsset>(),
                        CoveredLines = filteredParts.SelectMany(p => p.scenes).Sum(s => s.last - s.first + 1) };
                }
                int epScenes = result.Parts.SelectMany(p => p.scenes).Count();
                host.Children.Add(bar);
                host.Children.Add(StudioText("当前浏览：" + currentEpisode + " · " + epScenes + " 场 · " + result.Assets.Count + " 项资产", 13, 4));
            }
            else
            {
                host.Children.Add(bar);
            }
            var scenesPanel = StudioSection(null,"分场剧情拆解");
            var assetPanel = StudioSection(null,"待制作资产清单");
            foreach (var part in result.Parts)
            {
                if (!String.IsNullOrWhiteSpace(part.summary)) scenesPanel.Children.Add(ProjectAnalysisParagraph(part.summary, 14));
                if (!String.IsNullOrWhiteSpace(part.skillOutput))
                    scenesPanel.Children.Add(new Expander { Header = "查看完整拆解与待确认事项", Foreground = Window.Foreground,
                        Content = ProjectAnalysisParagraph(part.skillOutput, 14) });
                foreach (var scene in part.scenes)
                {
                    scenesPanel.Children.Add(ProjectAnalysisParagraph(scene.title + "\n" + scene.action, 14));
                    scenesPanel.Children.Add(SourceLink(source, scene.first, scene.last, false, returnToReview));
                }
            }
            AddAnalysisAssets(result, source, false, returnToReview, assetPanel);
            host.Children.Add(StudioColumns(scenesPanel,assetPanel,1.25,1,870));
        }

        private void ShowAssetHandoff() { ShowAssetPage(null); }

        private void ShowAssetPage(string detailId)
        {
            assetDetailId = detailId;
            BeginPage(1, Stage.All[1].Name, DisplayName(), 2);
            ScriptDocument source;
            AnalysisLedger ledger;
            if (!ReadProjectAnalysis(out source, out ledger) || source == null) { content.Children.Add(Notice("请先导入剧本并确认分析清单。")); return; }
            var confirmed = ledger.Confirmed(source);
            if (confirmed == null)
            {
                content.Children.Add(Notice("等待人工确认当前剧本的分析清单。"));
                return;
            }
            var result = ReadAnalysisResult(confirmed);
            if (result == null) return;
            if (detailId == null)
                content.Children.Add(ProjectAnalysisParagraph("已确认剧本分析第 " + confirmed.Number + " 版 · " +
                    Path.GetFileName(source.Source) + " · " + result.Assets.Count + " 项资产", 13));
            AddAssetProduction(projectFolder, source, confirmed, result);
        }

        private AnalysisResult ReadAnalysisResult(AnalysisVersion version)
        {
            try { return AnalysisData.ReadResult(projectFolder, version); }
            catch (Exception e)
            {
                content.Children.Add(Notice(AgentData.Short(e.Message, 350)));
                return null;
            }
        }

        private AnalysisResult ReadFullAnalysisResult(AnalysisVersion version)
        {
            try { return AnalysisData.ReadFullResult(projectFolder, version); }
            catch (Exception e)
            {
                content.Children.Add(Notice(AgentData.Short(e.Message, 350)));
                return null;
            }
        }

        private Button SourceLink(ScriptDocument source, int first, int last, bool handoff, Action returnAction = null)
        {
            var link = ActionButton("对照原文 " + source.LineLocation(first, last), delegate
            {
                if (!handoff) { switchAnalysisTab(true,first,source); return; }
                ShowScriptReader("返回资产交接",
                    returnAction ?? (handoff ? (Action)ShowAssetHandoff : ShowAnalysisReview), first, source);
            }, false);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            return link;
        }

        private void AddAnalysisAssets(AnalysisResult result, ScriptDocument source, bool handoff, Action returnAction = null, StackPanel host = null)
        {
            foreach (string category in AnalysisData.Categories)
            {
                var items = result.Assets.Where(a => a.category == category).ToList();
                var card = new StackPanel { Margin = new Thickness(14, 8, 14, 11) };
                card.Children.Add(Title(category + " · " + items.Count, 17));
                foreach (var asset in items)
                {
                    card.Children.Add(ProjectAnalysisParagraph(asset.name + " · " + asset.reason, 14));
                    var quotes = new ItemsControl();
                    foreach (int line in asset.evidence)
                    {
                        quotes.Items.Add(SourceLink(source, line, line, handoff, returnAction));
                        quotes.Items.Add(ProjectAnalysisParagraph(source.Lines[line - 1], 12));
                    }
                    card.Children.Add(new Expander { Header = "查看 " + asset.evidence.Count + " 处原文依据", Content = quotes,
                        Foreground = Window.Foreground, Margin = new Thickness(0, 0, 0, 5) });
                }
                var panel = Panel(card);
                panel.Margin = new Thickness(0, 8, 0, 4);
                (host ?? content).Children.Add(panel);
            }
        }

        private TextBlock ProjectAnalysisParagraph(string text, double size)
        {
            var paragraph = Muted(text, size, 6);
            paragraph.TextWrapping = TextWrapping.Wrap;
            return paragraph;
        }

        private string AnalysisState(string folder, ProjectProgress progress)
        {
            if (progress == null || String.IsNullOrEmpty(progress.Script)) return "尚未导入剧本";
            try
            {
                var source = AnalysisData.CurrentScript(folder);
                var ledger = AnalysisData.Load(folder);
                var latest = ledger.Current(source);
                var confirmed = ledger.Confirmed(source);
                if (latest != null && latest != confirmed) return "分析待审核";
                if (confirmed != null) return "资产清单已交接";
                if (AssetRunning(folder, "剧本分析")) return "分析中";
                if (ledger.LastRun != null && ledger.LastRun.Matches(source) && ledger.LastRun.State != "已完成")
                    return "分析待重试";
                return "等待分析";
            }
            catch (Exception) { return "请核对剧本或分析记录"; }
        }
    }
}
