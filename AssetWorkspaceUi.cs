using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private string assetDetailId;
        private string assetFilter = "全部";

        private bool AssetMatches(AssetItem item)
        {
            if (assetFilter=="全部") return true;
            if (!AssetProduction.PromptReady(item)) return assetFilter=="待核实";
            return assetFilter==(item.Active==null || item.Active.State=="未提交" ? "待出图" : item.Active.State);
        }

        private void ChangeProjectStyle(string root, AnalysisVersion version,AnalysisResult analysis,AssetWork work)
        {
            var panel=new StackPanel();
            var box=FormDialog("更换全片画风",panel);
            var style=AgentField(panel,"画风与尺寸要求",work.Style,100);
            string file=null;
            bool clear=false;
            var reference=StudioText(work.ReferencePath ?? "尚无全局参考图",13,6);
            panel.Children.Add(reference);
            panel.Children.Add(ActionButton("选择或更换全局参考图",delegate {
                var picker=new Microsoft.Win32.OpenFileDialog {Filter="参考图|*.png;*.jpg;*.jpeg;*.webp"};
                if(picker.ShowDialog(box)==true){file=picker.FileName;clear=false;reference.Text=file;}
            },false));
            panel.Children.Add(ActionButton("清除参考图",()=>{clear=true;file=null;reference.Text="已选择清除参考图";},false));
            bool directSpecs = AssetProduction.UsesDirectSpecs(root);
            var regenerate=new CheckBox {Content=directSpecs ? "清空旧共用基础图并按新画风更新资产设定" : "清空旧共用基础图并立即按新画风重新生成全部提示词",IsChecked=true};
            panel.Children.Add(regenerate);
            panel.Children.Add(ActionButton("保存画风",delegate {
                bool redo=regenerate.IsChecked==true;
                string value=style.Text.Trim();
                if(!UpdateAsset(root,version,analysis,fresh=>{
                    if(redo) {
                        AssetProduction.InvalidateImageJobs(fresh,j=>true);
                        if(fresh.BaseImages!=null)fresh.BaseImages.Clear();
                        foreach(var i in fresh.Items) {
                            i.ActiveId=null; i.PendingRepair=null;
                            if (directSpecs && (i.PromptOrigin ?? "").Contains("人工")) continue;
                            i.Conflict="画风已更新，等待按新画风重新准备提示词。";
                        }
                        fresh.DeliveredId=null;
                        fresh.PendingPromptIds=fresh.Items.Select(i=>i.Id).ToList();
                        fresh.PromptRequestIds=fresh.PendingPromptIds.ToList();
                        fresh.PromptRequestStyle=value;fresh.PromptRequestSkill=null;
                    }
                    if(file!=null)AssetProduction.SetReference(root,fresh,file);
                    else if(clear){fresh.ReferencePath=null;fresh.ReferenceDigest=null;}
                    fresh.Style=value;
                    if (redo && directSpecs) AssetProduction.PopulateDirectImageSpecs(root, fresh, analysis);
                },"交付"))return;
                assetStyleDraft=value; box.Close();
                if(redo && !directSpecs)PrepareAssets(root,version,analysis,work.Items.Select(i=>i.Id).ToArray(),value);
            },true));
            panel.Children.Add(ActionButton("取消",()=>box.Close(),false)); box.ShowDialog();
        }

        private readonly HashSet<string> checkedImageRecoveries = new HashSet<string>();
        private void CheckOriginalImages(string root, AnalysisVersion version, AnalysisResult analysis, string lane)
        {
            RunAsset(root, version, analysis, async (current, cancel) =>
            {
                var batch = current.Batch(lane);
                if (batch == null) return "没有待核对的批次。";
                SetAssetProgress(root, "正在检查原任务的回执与图片", batch.Jobs.Count(j => j.Done), batch.Jobs.Count, lane);
                await Task.Run(() => AssetProduction.ReconcileBatch(root, current, batch, cancel));
                if (lane != "出图" && !batch.NeedsAction)
                {
                    current.StoreBatch(lane, null);
                    AssetProduction.Save(root, current);
                }
                SetAssetProgress(root, AssetProduction.PendingSummary(batch), batch.Jobs.Count(j => j.Done), batch.Jobs.Count, lane);
                return AssetProduction.PendingSummary(batch);
            }, false, true, lane);
        }
        private readonly HashSet<string> collapsedAssetCategories = new HashSet<string>();
        private string assetStyleContext;
        private string assetStyleDraft;
        private string assetSavedStyle;
        private bool assetSettingsOpen;
        private string assetProgressProject;
        private string assetProgressText;
        private Func<AssetImageBatch> readProgressBatch;
        private TextBlock assetBatchStatus;
        private sealed class AssetTask
        {
            internal string Root, Name, Status;
            internal volatile string Channel, Model;
            internal string TimedStatus {get{return CountedStatus+" · "+(int)Clock.Elapsed.TotalSeconds+" 秒";}}
            internal int Done, Total;
            internal void UpdateAnalysis(int done, int total, string detail)
            {
                if (total > 0) { Done = Math.Max(Done,done); Total = total; }
                if (detail != null) { Status=detail; if(detail.Contains("exec"))Channel="exec";else if(detail.Contains("app-server"))Channel="app-server";else if(detail.Contains("API"))Channel="API"; }
            }
            internal string CountedStatus { get { return Total > 0 ? Done + " / " + Total + " · " + Status : Status; } }
            internal StackPanel Row;
            internal TextBlock Label;
            internal ProgressBar Bar;
            internal Button Stop;
            internal readonly System.Threading.CancellationTokenSource Cancel = new System.Threading.CancellationTokenSource();
            internal readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        }
        private readonly List<AssetTask> assetTasks = new List<AssetTask>();
        private static System.Windows.Threading.DispatcherTimer taskPulse;
        private TextBlock flowStatusHeader;
        private void BeginAssetTask(AssetTask task)
        {
            assetTasks.Add(task);
            if(FlowCreation.Development){
                DevMonitor.Context=task.Root;DevMonitor.Operation=task.Name;
                DevMonitor.Route=(route,model)=>{task.Channel=route;task.Model=model;};
                DevMonitor.Progress(task.Root,"开始"+task.Name);
            }
            SyncTaskPulse();
        }
        private void EndAssetTask(AssetTask task)
        {
            assetTasks.Remove(task);
            if(FlowCreation.Development){
                DevMonitor.ClearContext();
                DevMonitor.Progress(task.Root,task.Name+"结束 · "+(int)task.Clock.Elapsed.TotalSeconds+" 秒");
            }
            SyncTaskPulse();
        }
        private void SyncTaskPulse()
        {
            if(openWorkspaces.Any(w=>w.assetTasks.Count>0)){
                if(taskPulse==null){
                    taskPulse=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
                    taskPulse.Tick+=delegate{shell.TickActiveProgress();};
                }
                taskPulse.Start();
            } else if(taskPulse!=null)taskPulse.Stop();
        }
        private void TickActiveProgress()
        {
            var w=active;
            w.DrawAssetProgress(w.projectFolder);
            w.TickFlowOverview();
            foreach(var t in w.assetTasks.Where(t=>SameProject(t.Root,w.projectFolder))){
                if(t.Name=="剧本分析" && w.analysisProgress!=null)w.analysisProgress.Text=t.TimedStatus;
            }
            if(w.IsShowingCreating()){
                var creating=w.assetTasks.FirstOrDefault(t=>t.Cancel==w.flowCreating);
                if(creating!=null)w.flowCreatingLabel.Text=creating.TimedStatus;
            }
            w.RefreshStatus();
        }
        private UIElement assetProgressPanel;
        private StackPanel assetTaskRows;
        private bool AssetRunning(string root, string name)
        {
            return assetTasks.Any(t => SameProject(t.Root, root) && t.Name == name);
        }

        private void AddAssetProgress(string root, Func<AssetImageBatch> readBatch = null)
        {
            readProgressBatch = readBatch;
            assetTaskRows = new StackPanel { Margin = new Thickness(14, 7, 14, 9) };
            var panel = new StackPanel();
            assetBatchStatus = ProjectAnalysisParagraph("", 13);
            assetBatchStatus.Margin = new Thickness(14, 7, 14, 0);
            panel.Children.Add(assetBatchStatus);
            panel.Children.Add(assetTaskRows);
            assetProgressPanel = Panel(panel);
            content.Children.Add(assetProgressPanel);
            DrawAssetProgress(root);
        }

        private void SetAssetProgress(string root, string message, int completed, int total, string name = "出图")
        {
            var task = assetTasks.FirstOrDefault(t => SameProject(t.Root, root) && t.Name == name);
            if (task != null) {
                if(FlowCreation.Development && task.Status!=message)DevMonitor.Progress(root,message);
                task.Status = message;
                if(message!=null && FlowCreation.Development)foreach(var channel in new[]{"app-server","exec","API","即梦","RapidOCR"})
                    if(message.Contains(channel)){task.Channel=channel;break;}
                task.Done = completed; task.Total = total; }
            assetProgressProject = root; assetProgressText = message;
            if (SameProject(root, projectFolder)) { DrawAssetProgress(root); RefreshStatus(); }
        }

        private void DrawAssetProgress(string root)
        {
            if (assetProgressPanel == null || !content.Children.Contains(assetProgressPanel)) return;
            var active = assetTasks.Where(t => SameProject(t.Root, root)).ToArray();
            string liveStatus = null;
            if (!AssetRunning(root, "出图") && readProgressBatch != null)
            {
                try
                {
                    var batch = readProgressBatch();
                    if (batch != null) liveStatus = AssetProduction.PendingSummary(batch);
                }
                catch (System.IO.IOException e) { liveStatus = "正在核对批次记录 · " + e.Message; }
                catch (System.IO.InvalidDataException e) { liveStatus = e.Message; }
            }
            bool showBatch = liveStatus != null;
            assetBatchStatus.Text = liveStatus ?? "";
            assetBatchStatus.Visibility = showBatch ? Visibility.Visible : Visibility.Collapsed;
            if (active.Length == 0)
            {
                assetTaskRows.Children.Clear();
                if (!showBatch && SameProject(root, assetProgressProject) && !String.IsNullOrWhiteSpace(assetProgressText))
                    assetTaskRows.Children.Add(ProjectAnalysisParagraph(assetProgressText, 13));
            }
            else
            {
                if (assetTaskRows.Children.Count != active.Length || active.Any(t => t.Row == null || t.Row.Parent != assetTaskRows))
                {
                    assetTaskRows.Children.Clear();
                    foreach (var task in active)
                    {
                        task.Row = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) }; task.Row.Children.Add(header);
                        task.Stop = ActionButton("停止" + task.Name, () => UiFx.SafeCancel(task.Cancel), false);
                        DockPanel.SetDock(task.Stop, Dock.Right); header.Children.Add(task.Stop);
                        task.Label = ProjectAnalysisParagraph("", 13); header.Children.Add(task.Label);
                        task.Bar = new ProgressBar { Height = 6, Foreground = Brush("Accent"), Background = Brush("Input") };
                        AutomationProperties.SetName(task.Bar, task.Name + "进度");
                        task.Row.Children.Add(task.Bar);
                        assetTaskRows.Children.Add(task.Row);
                    }
                }
                foreach (var task in active)
                {
                    string count = task.Total > 0 ? " · " + task.Done + " / " + task.Total : "";
                    task.Label.Text = task.Name + count + " · " + (int)task.Clock.Elapsed.TotalSeconds + " 秒\n" + task.Status;
                    task.Stop.IsEnabled = !UiFx.IsCancelled(task.Cancel);
                    task.Bar.Maximum = Math.Max(1, task.Total);
                    task.Bar.Value = task.Done; task.Bar.IsIndeterminate = task.Total == 0;
                }
            }
            assetProgressPanel.Visibility = !showBatch && assetTaskRows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddImageRecovery(string root, AnalysisVersion version, AnalysisResult analysis,
            string lane, AssetImageBatch selected, string[] pending, Func<AssetChannel> chosen)
        {
            if (!AssetRunning(root, lane))
            {
                if (selected != null)
                {
                    var history = selected.Jobs.Where(j => j.IsBase && j.EarlierBaseAttempts != null)
                        .SelectMany(j => j.EarlierBaseAttempts).Where(a => a.State == "已退回").ToList();
                    if (!selected.NeedsAction && history.Count == 0) return;
                    content.Children.Add(Title("出图批次 · " + String.Join("、", selected.Jobs.Where(j => !j.IsBase).Select(j => j.Name).Take(3)), 15));
                    AddBaseHistory(content, root, "已退回的基础图历史", history);
                    if (!selected.NeedsAction) return;
                }
                if (selected != null)
                    foreach (var basis in selected.Jobs.Where(j => j.IsBase && j.Attempt != null && j.Attempt.State == "待审图"))
                    {
                        var review = new StackPanel { Margin = new Thickness(14) };
                        review.Children.Add(Title("审核基础图 · " + basis.Name, 17));
                        AddAssetPreview(review, root, basis.Attempt);
                        var choices = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                        string batchId = selected.Id, attemptId = basis.Attempt.Id;
                        choices.Children.Add(ActionButton("采用基础图", () => UpdateAsset(root, version, analysis,
                            w => AssetProduction.ReviewBase(root, w, batchId, attemptId, true), lane), true));
                        var unlocked = selected.Jobs.Where(j => !j.IsBase && j.GroupId == basis.GroupId && j.Attempt == null)
                            .Select(j => j.ItemId).ToArray();
                        if (unlocked.Length > 0)
                            choices.Children.Add(ActionButton("采用并继续生成同组造型（" + unlocked.Length + " 项）", delegate {
                                if (!UpdateAsset(root, version, analysis, w => AssetProduction.ReviewBase(root, w, batchId, attemptId, true), lane)) return;
                                RunImageBatch(root, version, analysis, unlocked, chosen(), "恢复", lane, batchId, basis.GroupId);
                            }, true));
                        choices.Children.Add(ActionButton("退回基础图", () => RejectBaseDialog(root, version, analysis,
                            lane, batchId, basis), false));
                        review.Children.Add(choices);
                        content.Children.Add(Panel(review));
                    }
                var recovery = new WrapPanel { Margin = new Thickness(0, 3, 0, 7) };
                bool hasAttempts = selected != null && selected.Jobs.Any(j => !j.Done && j.Attempt != null);
                if (selected == null || AssetProduction.ReadyJobs(selected).Any())
                    recovery.Children.Add(ActionButton(selected == null ? "继续生成待出图资产（" + pending.Length + " 项）" :
                        AssetProduction.ResumeLabel(selected), () => RunImageBatch(root, version, analysis, pending, chosen(),
                            "恢复", lane, selected == null ? null : selected.Id), true));
                if (hasAttempts || selected == null)
                {
                    if (selected != null)
                    {
                        recovery.Children.Add(ActionButton("检查原任务回图", () => CheckOriginalImages(root, version, analysis, lane), false));
                        string key = root + "|" + selected.Id + "|" + String.Join(",",
                            selected.Jobs.Where(j => !j.Done && j.Attempt != null).Select(j => j.Attempt.Id));
                        if (!systemBusy && !checkedImageRecoveries.Contains(key))
                        {
                            var page = assetProgressPanel;
                            Window.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (SameProject(root, projectFolder) && content.Children.Contains(page) && !AssetRunning(root, lane) && checkedImageRecoveries.Add(key))
                                    CheckOriginalImages(root, version, analysis, lane);
                            }));
                        }
                    }
                    recovery.Children.Add(ActionButton("仅重试未完成项", () => RunImageBatch(root, version, analysis, pending, chosen(), "重新生成", lane), true));
                }
                recovery.Children.Add(ActionButton("取消任务", () => DiscardPausedImages(root, version, analysis, lane), false));
                content.Children.Add(recovery);
            }
        }

        private void FillAssetChannels(ComboBox box, List<AssetChannel> platforms, bool showStatus)
        {
            box.Items.Add(new ComboBoxItem { Content = "Codex 内置图片生成", Tag = "builtin" });
            box.Items.Add(new ComboBoxItem { Content = FlowChannels.ResponsesApiName, Tag = FlowChannels.ResponsesApi });
            foreach (var platform in platforms)
                box.Items.Add(new ComboBoxItem { Content = platform.Name + (showStatus ? " · " + (platform.Status ?? "待检测") : ""), Tag = platform.Id });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == assetChannelId) ?? box.Items[0];
        }

        private void AddAssetProduction(string root, ScriptDocument script, AnalysisVersion version, AnalysisResult analysis)
        {
            AssetWork work;
            List<AssetChannel> platforms;
            string recordPath;
            DateTime loadedAt;
            try
            {
                recordPath = System.IO.Path.Combine(AssetProduction.Folder(root, version.Id), "制作记录.json");
                loadedAt = System.IO.File.GetLastWriteTimeUtc(recordPath);
                work = AssetProduction.Load(root, version, analysis);
                platforms = AssetChannels.Load();
            }
            catch (Exception e)
            {
                content.Children.Add(Notice(AgentData.Short(e.Message, 360)));
                return;
            }
            if (work.Items.Count == 0)
            {
                content.Children.Add(ReviewAllButton(ReviewScope.Assets, ShowAssetHandoff));
                content.Children.Add(Notice("本版分析没有需要出图的资产。请核对剧本分析，或确认空清单并交接。"));
                content.Children.Add(ActionButton(work.DeliveredId == null ? "确认无资产图并交接" : "查看已交接版本", delegate
                {
                    if (work.DeliveredId != null) { ShowStage(2); return; }
                    string answer;
                    if (Prompt("确认空资产清单", "本版没有人物、场景或道具图，仍将交接一份空清单。", "确认交接", "返回核对", false, out answer)
                        && UpdateAsset(root, version, analysis, current => AssetProduction.Deliver(root, current), "交付")) ShowStage(2);
                }, true));
                return;
            }
            int unverified = work.Items.Count(i=>!AssetProduction.PromptReady(i));
            StageStatus(2,work.Items.Count(i=>i.Active!=null && i.Active.State=="已通过") + "/" + work.Items.Count +
                " 张已通过"+(unverified==0 ? "" : " · "+unverified+" 项待核实"));
            string styleContext = root + "|" + version.Id;
            if (assetStyleContext != styleContext)
            {
                assetStyleContext = styleContext;
                assetStyleDraft = (work.PendingPromptIds == null ? work.Style : work.PromptRequestStyle) ?? "";
                assetSettingsOpen = false;
                assetTargetSize = null;
                var pendingBatch = work.Batches().Select(p => p.Value).FirstOrDefault(b => b.NeedsAction);
                var config = FlowCreation.IsFlowProject(root) ? FlowCreation.Config(root) : null;
                assetChannelId = pendingBatch != null ? pendingBatch.ChannelId :
                    config != null && !String.IsNullOrWhiteSpace(config.Channel) ? config.Channel : "builtin";
                collapsedAssetCategories.Clear();
            }
            else if (work.PendingPromptIds == null && assetStyleDraft == assetSavedStyle)
                assetStyleDraft = work.Style ?? "";
            assetSavedStyle = work.Style ?? "";
            var activeBatch = work.Batches().Select(p => p.Value).FirstOrDefault(b => b.NeedsAction);
            bool legacy = !work.Batches().Any() && work.Items.Any(i => i.Active != null &&
                (i.Active.State == "需核对" || i.Active.State == "运行中"));
            if (!AssetRunning(root, "出图") && (activeBatch != null || legacy))
            {
                assetProgressProject = root;
                assetProgressText = activeBatch == null ? "有历史出图需要核对。" : AssetProduction.PendingSummary(activeBatch);
            }
            AddAssetProgress(root, delegate {
                var writtenAt = System.IO.File.GetLastWriteTimeUtc(recordPath);
                if (loadedAt != writtenAt)
                {
                    var latest = AssetProduction.Load(root, version, analysis);
                    activeBatch = latest.Batches().Select(p => p.Value).FirstOrDefault(b => b.NeedsAction);
                    if (activeBatch == null && SameProject(root, assetProgressProject) && latest.ImageBatch != null)
                        assetProgressText = AssetProduction.PendingSummary(latest.ImageBatch);
                    loadedAt = writtenAt;
                }
                return activeBatch;
            });

            Func<AssetChannel> chosen = () => FlowChannels.IsResponsesApi(assetChannelId) ? FlowChannels.ResponsesApiChannel() : platforms.FirstOrDefault(p => p.Id == assetChannelId);
            var assigned = new HashSet<string>(work.Batches().Where(p => p.Value.NeedsAction).SelectMany(p =>
                p.Value.Jobs.Where(j => !j.IsBase && !j.Done).Select(j => j.ItemId)));
            string[] pending = work.Items.Where(i => PendingImage(i) && !assigned.Contains(i.Id)).Select(i => i.Id).ToArray();
            var item = work.Items.FirstOrDefault(i => i.Id == assetDetailId);
            var tabs=new WrapPanel();
            tabs.Children.Add(ViewTab("资产图册总览",item==null,()=>ShowAssetPage(null)));
            var monitor=ViewTab("大图监看台",item!=null,()=>ShowAssetPage(work.Items.First(AssetMatches).Id));
            monitor.IsEnabled=work.Items.Any(AssetMatches);
            tabs.Children.Add(monitor);
            tabs.Children.Add(ActionButton("更换全片画风",()=>ChangeProjectStyle(root,version,analysis,work),false));
            content.Children.Add(tabs);
            var filters=new WrapPanel();
            foreach(string state in new[] {"全部","待核实","待出图","待审图","已通过","已退回"})
                filters.Children.Add(ViewTab(state,assetFilter==state,delegate {
                    assetFilter=state;
                    var match=work.Items.FirstOrDefault(AssetMatches);
                    ShowAssetPage(item==null || match==null ? null : match.Id);
                }));
            content.Children.Add(filters);
            if (assetFilter == "已退回" && work.BaseHistory != null)
            {
                foreach (var pair in work.BaseHistory)
                {
                    var group = (work.Groups ?? new List<AssetGroup>()).FirstOrDefault(g => g.Key == pair.Key);
                    AddBaseHistory(content, root, "已退回 · " + (group == null ? pair.Key : group.name) + "共用基础图",
                        pair.Value.Where(a => a.State == "已退回").ToList());
                }
            }
            foreach (var entry in work.Batches())
                AddImageRecovery(root, version, analysis, entry.Key, entry.Value, pending, chosen);
            if (legacy) AddImageRecovery(root, version, analysis, "出图", null, pending, chosen);
            var shown = work.Items.ToDictionary(i => i.Id, i => i.ActiveId);
            bool ready = work.Items.Count > 0 && work.Items.All(i=>i.Active!=null && (i.Active.State=="已通过" || i.Active.State=="待审图"));
            bool flow = FlowCreation.IsFlowProject(root);
            string passLabel = flow ? "一键确认全部资产并继续制作" : "一键通过全部资产并进入分镜";
            Action deliver = () =>
            {
                if (!UpdateAsset(root, version, analysis, fresh => AssetProduction.ApproveAndDeliver(root, fresh, shown), "交付")) return;
                if (!flow) { ShowStage(2); return; }
                TryAction(delegate {
                    var latest = AssetProduction.Load(root, version, analysis);
                    if (latest.Items.Any(i => i.ActiveId != shown[i.Id] || i.Active.State != "已通过"))
                        throw new InvalidOperationException("图片已变化，请重新核对后继续制作。");
                    string reviewKey = FlowProduction.ReviewKey(root, latest);
                    StartFlow(root, reviewKey);
                });
            };
            Func<bool, Button> passButton = primary => {
                string caption = passLabel + (flow || primary ? " ›" : "");
                var button = ActionButton(caption, deliver, primary);
                AutomationProperties.SetName(button, caption);
                return button;
            };
            var delivered = work.DeliveredId == null || !ready ? null : AssetProduction.ReadDeliveryRecord(root,work);
            bool handed = delivered != null && delivered.Items.Count == shown.Count && delivered.Items.All(i=>shown[i.Id]==i.ActiveId);
            bool showPass = ready && (!handed || flow && FlowCreation.State(root).Phase == "资产审核");
            if (showPass)
            {
                var footer = new WrapPanel();
                footer.Children.Add(passButton(true));
                SetFooter("全部 "+shown.Count+" 项资产图已就绪", footer);
            }
            if (item != null)
            {
                var singleOptions = new WrapPanel { Margin = new Thickness(0, 4, 0, 5), IsEnabled = true };
                Action<AssetChannel> changeSingleChannel;
                var channel = new ComboBox { MinWidth = 145 };
                FillAssetChannels(channel, platforms, false);
                var singleSizes=MakeImageSizes(chosen(),out changeSingleChannel);
                channel.SelectionChanged+=delegate {
                    var selected = channel.SelectedItem as ComboBoxItem;
                    if (selected == null) return;
                    assetChannelId = (string)selected.Tag;
                    changeSingleChannel(chosen());
                };
                singleOptions.Children.Add(channel);
                singleOptions.Children.Add(singleSizes);
                singleOptions.Children.Add(SkillControls(root, ImageOutputSettings.SkillOperation, "图片生成"));
                var tools = new StackPanel();
                var history=AssetTools(root,script,version,analysis,item,chosen,work,tools);
                tools.Children.Add(singleOptions);
                AddMonitorReview(root,script,version,analysis,work,item,tools);
                content.Children.Add(new Expander {Header="提示词全文、Skill 交付原文、原文依据与历次制作记录",Content=history,Foreground=Window.Foreground});
                return;
            }
            assetDetailId = null;
            int drafted = work.Items.Count(AssetProduction.PromptReady);
            int awaitingReview = work.Items.Count(i => i.Active != null && i.Active.State == "待审图");
            int approved = work.Items.Count(i => i.Active != null && i.Active.State == "已通过");
            var library = StudioSection(null, "资产图册");
            var operations = StudioSection(null, "制作与交接");
            var summary = AssetMetrics(drafted, awaitingReview, approved, work.Items.Count);
            content.Children.Add(summary);
            content.Children.Add(StudioColumns(library, operations, 1.9, 1, 870));
            var passAll = passButton(false);
            passAll.IsEnabled = ready;
            var reviewActions = new WrapPanel();
            if (showPass) reviewActions.Children.Add(passButton(true));
            operations.Children.Add(reviewActions);
            var bulk = new StackPanel();
            bulk.Children.Add(passAll);
            bulk.Children.Add(ReviewAllButton(ReviewScope.Assets, ShowAssetHandoff));
            if (!ready) operations.Children.Add(Muted("还有 " + (work.Items.Count - approved - awaitingReview) + " 项需要生成图片或处理退回，全部就绪后即可一键通过。", 13, 5));
            var usagePanel = new StackPanel();
            var usage = new Expander { Header = "资产任务与额度消耗记录", Content = usagePanel, Foreground = Window.Foreground, Margin = new Thickness(0, 8, 0, 8) };
            usage.Expanded += delegate {
                usagePanel.Children.Clear();
                TryAction(() => {
                    var records = AssetUsageLedger.Recent(root, version.Id);
                    if (records.Count == 0) usagePanel.Children.Add(Muted("暂无用量记录", 13, 6));
                    foreach (var record in records) usagePanel.Children.Add(ProjectAnalysisParagraph(record.Describe(), 13));
                });
            };
            operations.Children.Add(usage);
            var conflicts = work.Items.Where(i => !AssetProduction.PromptReady(i)).ToList();
            if (conflicts.Count > 0)
            {
                operations.Children.Add(AutoReviewButton(root, version, analysis, conflicts, true));
                operations.Children.Add(ActionButton("查看待核实的提示词 · " + conflicts.Count + " 项", delegate
                {
                    ShowBlockedAssetPrompts(root, version, analysis);
                }, false));
            }

            var uncheckedItems = conflicts.Where(i => AssetProduction.CurrentRepair(i, i.PromptStyle ?? work.Style) == null).ToList();
            if (conflicts.Count == 0 && work.LastAutoReview != null)
                operations.Children.Add(ActionButton("查看本次 Codex 自动审核摘要", () => ShowBlockedAssetPrompts(root, version, analysis), false));
            if (uncheckedItems.Count > 0)
                operations.Children.Add(ActionButton("一键 Codex 核实 · " + uncheckedItems.Count + " 项", () =>
                {
                    ShowBlockedAssetPrompts(root, version, analysis);
                    StartPromptChecks(root, version, analysis, uncheckedItems.Select(x => x.Id).ToArray());
                }, false));
            var production = new StackPanel { Margin = new Thickness(14, 8, 14, 12), IsEnabled = true };
            var batch = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            bool resumePrompts = work.PendingPromptIds != null && work.PendingPromptIds.Count > 0;
            var missing = resumePrompts ? work.PendingPromptIds.ToArray() :
                work.Items.Where(i => String.IsNullOrWhiteSpace(i.Prompt) && String.IsNullOrWhiteSpace(i.Conflict)).Select(i => i.Id).ToArray();
            var prepare = ActionButton(missing.Length > 0 ? (resumePrompts ? "继续准备提示词 · " : "准备提示词 · ") + missing.Length + " 项" : "重新准备提示词", delegate
            {
                if (missing.Length == 0)
                {
                    string answer;
                    if (!Prompt("重新生成全部提示词", "将按当前剧本重新生成本版所有资产的提示词。"
                        + "已有图片及其原始提交提示词可在详情里查看。",
                        "重新生成", "取消", false, out answer)) return;
                }
                PrepareAssets(root, version, analysis, missing.Length == 0 ? work.Items.Select(i => i.Id).ToArray() : missing, assetStyleDraft);
            }, !ready && missing.Length > 0);
            prepare.IsEnabled = work.Items.Count != 0;
            bool directSpecs = AssetProduction.UsesDirectSpecs(root);
            if (!directSpecs) batch.Children.Add(prepare);
            var setup = new StackPanel();
            if (!directSpecs)
            {
                setup.Children.Add(Muted("生成提示词时使用", 13, 6));
                setup.Children.Add(SkillControls(root, LocalSkills.StageKey(1), "资产提示词"));
            }

            int blockedCount = work.Items.Count(i => pending.Contains(i.Id) && !AssetProduction.PromptReady(i));
            string generateLabel = blockedCount > 0 ? "核对出图条件 · " + blockedCount + " 项" :
                pending.Length > 0 ? "生成资产图 · " + pending.Length + " 项" : "生成资产图";
            var generate = ActionButton(generateLabel, () => GenerateAssets(root, version, analysis, pending, chosen()),
                !ready && missing.Length == 0 && pending.Length > 0);
            generate.IsEnabled = pending.Length != 0 && !legacy;
            generate.ToolTip = blockedCount > 0 ? "查看缺少或待核实的提示词，逐项处理后可提交。"
                : "开始前可预览每项提示词、渠道、张数和费用估算。";
            if (pending.Length != 0) batch.Children.Add(generate);
            if (awaitingReview > 0)
            {
                var next = work.Items.First(i => i.Active != null && i.Active.State == "待审图");
                batch.Children.Add(ActionButton("审图 · " + awaitingReview + " 张", () => ShowAssetPage(next.Id),
                    !ready && missing.Length == 0 && pending.Length == 0));
            }

            var channels = new ComboBox { MinWidth = 215, Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetName(channels, "图片生成渠道");
            FillAssetChannels(channels, platforms, true);
            assetChannelId = (string)((ComboBoxItem)channels.SelectedItem).Tag;
            Action<AssetChannel> changeSizeChannel;
            var sizes = MakeImageSizes(chosen(), out changeSizeChannel);
            channels.SelectionChanged += delegate
            {
                var selected = channels.SelectedItem as ComboBoxItem;
                if (selected != null) assetChannelId = (string)selected.Tag;
                changeSizeChannel(chosen());
            };
            setup.Children.Add(Muted("图片生成渠道与输出尺寸", 13, 8));
            var route = new WrapPanel();
            route.Children.Add(channels);
            route.Children.Add(sizes);
            setup.Children.Add(route);
            setup.Children.Add(SkillControls(root, ImageOutputSettings.SkillOperation, "图片生成"));
            production.Children.Add(batch);
            setup.Children.Add(ProjectAnalysisParagraph(
                "内置渠道按目标规格构图，实际像素以回图为准。外部 CLI 只显示已配置的规格。", 12));
            AddAssetSettings(production, root, version, analysis, work, platforms.Count, chosen, setup);
            operations.Children.Add(Panel(production));
            operations.Children.Add(new Expander { Header = "批量审核与阶段交接", Content = bulk, Foreground = Window.Foreground,
                Margin = new Thickness(0, 8, 0, 0) });

            string filter = "";
            var search = new TextBox { Height = 36, MinWidth = 230, MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(search, "按资产名查找");
            library.Children.Add(Muted("按资产名查找", 13, 12));
            library.Children.Add(search);
            var sections = new List<Action>();
            var empty = Muted("没有找到匹配的资产。", 13, 6);
            var stateParents = AssetProduction.StateParents(work);
            foreach (string category in AnalysisData.Categories)
            {
                var group = work.Items.Where(i => i.Source.category == category && AssetMatches(i)).ToList();
                if (group.Count == 0) continue;
                var rows = new WrapPanel { Margin = new Thickness(8, 4, 4, 6) };

                var entries = new Dictionary<Button, string>();
                foreach (var asset in group)
                {
                    string state = asset.Active == null ? "待出图" : asset.Active.State;
                    if (!String.IsNullOrWhiteSpace(asset.Conflict)) state = "提示词待核实";
                    else if (String.IsNullOrWhiteSpace(asset.Prompt) && asset.Active == null) state = "待提示词";
                    var info = new DockPanel { LastChildFill = true };
                    var art = new Border { Width = 72, Height = 54, CornerRadius = new CornerRadius(6),
                        Background = Brush("Input"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true };
                    art.Child = AssetThumbnail(root, category, asset.Active);
                    AddLeft(info, art);
                    var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var label = Title(asset.Source.name, 14);
                    label.TextTrimming = TextTrimming.CharacterEllipsis;
                    label.TextWrapping = TextWrapping.NoWrap;
                    details.Children.Add(label);
                    string relationship = AssetProduction.StateLabel(stateParents, asset);
                    if (relationship != null) details.Children.Add(Muted(relationship, 12, 2));
                    var stateText = Muted(state, 12, 4);
                    if (state == "待审图" || state == "提示词待核实") stateText.Foreground = Brush("Review");
                    else if (state == "已通过") stateText.Foreground = Brush("AccentText");
                    details.Children.Add(stateText);
                    info.Children.Add(details);
                    var open = ActionButton(info, () => ShowAssetPage(asset.Id), false);
                    open.Style = Style("CardButton");
                    open.Width = 212;
                    open.Padding = new Thickness(9, 8, 10, 8);
                    open.Margin = new Thickness(0, 4, 8, 4);
                    open.ToolTip = asset.Source.name + " · " + state;
                    AutomationProperties.SetName(open, asset.Source.name + " · " + state + " · 查看资产详情");
                    entries.Add(open, asset.Source.name);
                    rows.Children.Add(open);
                }
                var categoryPanel = new Expander { Header = category + " · " + group.Count, Content = rows,
                    IsExpanded = !collapsedAssetCategories.Contains(category), Foreground = Window.Foreground,
                    Margin = new Thickness(0, 10, 0, 4) };
                categoryPanel.Expanded += delegate { collapsedAssetCategories.Remove(category); };
                categoryPanel.Collapsed += delegate { collapsedAssetCategories.Add(category); };
                library.Children.Add(categoryPanel);
                sections.Add(delegate {
                    int visible = 0;
                    foreach (var entry in entries)
                    {
                        bool hit = entry.Value.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                        entry.Key.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                        if (hit) visible++;
                    }
                    categoryPanel.Visibility = visible == 0 ? Visibility.Collapsed : Visibility.Visible;
                    if (filter.Length > 0 && visible > 0) categoryPanel.IsExpanded = true;
                });
            }
            empty.Visibility = work.Items.Any(AssetMatches) ? Visibility.Collapsed : Visibility.Visible;
            library.Children.Add(empty);
            search.TextChanged += delegate {
                filter = search.Text.Trim();
                foreach (var update in sections) update();
                empty.Visibility = !work.Items.Any(i => AssetMatches(i) && i.Source.name.IndexOf(filter,
                    StringComparison.CurrentCultureIgnoreCase) >= 0) ? Visibility.Visible : Visibility.Collapsed;
            };


        }

        private UIElement AssetThumbnail(string root, string category, AssetAttempt attempt)
        {
            if (attempt != null && !String.IsNullOrEmpty(attempt.Path))
            {
                try
                {
                    var bitmap = LoadBitmapFile(AgentData.Inside(root, attempt.Path), 140);
                    return new Image { Source = bitmap, Stretch = System.Windows.Media.Stretch.UniformToFill };
                }
                catch (Exception e)
                {
                    if (!(e is System.IO.IOException || e is InvalidOperationException || e is NotSupportedException ||
                        e is UnauthorizedAccessException || e is FormatException || e is ArgumentException)) throw;
                }
            }
            var icon = Icon(category == "人物" ? "\uE77B" : category == "场景" ? "\uEB9F" : "\uE7B8", 21);
            icon.Foreground = Brush("AccentText");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return icon;
        }

        private void AddAssetSettings(StackPanel panel, string root, AnalysisVersion version, AnalysisResult analysis,
            AssetWork work, int platformCount, Func<AssetChannel> chosen, StackPanel setup)
        {
            var settings = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            settings.Children.Add(setup);
            var expand = new Expander { Header = "制作设置 · Skill、渠道与画风", Content = settings, IsExpanded = assetSettingsOpen,
                Foreground = Window.Foreground };
            expand.Expanded += delegate { assetSettingsOpen = true; };
            expand.Collapsed += delegate { assetSettingsOpen = false; };
            panel.Children.Add(expand);
            settings.Children.Add(Muted("单张出图等待上限（分钟）", 13, 6));
            var deadline = new ComboBox { ItemsSource = Enumerable.Range(2, 29).ToArray(), Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = ImageOutputSettings.TimeoutMinutes(Sys.ImageTimeoutMinutes) };
            AutomationProperties.SetName(deadline, "单张出图等待上限（分钟）");
            deadline.SelectionChanged += delegate
            {
                if (!(deadline.SelectedItem is int)) return;
                TryAction(() => { var opts = Sys.Copy(); opts.ImageTimeoutMinutes = (int)deadline.SelectedItem; SaveSystemOptions(opts); });
            };
            settings.Children.Add(deadline);
            var retry = new CheckBox { Content = "内置出图超时且无回图时，允许自动再提交一次（可能增加用量）",
                IsChecked = Sys.AllowBuiltinTimeoutRetry, Foreground = Window.Foreground, Margin = new Thickness(0, 5, 0, 3) };
            retry.Click += delegate { TryAction(() => { var opts = Sys.Copy(); opts.AllowBuiltinTimeoutRetry = retry.IsChecked == true; SaveSystemOptions(opts); }); };
            settings.Children.Add(retry);
            var style = AgentField(settings, "画风与尺寸要求", assetStyleDraft, 60);
            style.TextChanged += delegate { assetStyleDraft = style.Text; };
            settings.Children.Add(ProjectAnalysisParagraph(platformCount == 0 ? "外部平台 CLI · 无" :
                "外部平台 CLI · " + platformCount + " 家", 13));
            var tools = new WrapPanel { Margin = new Thickness(0, 5, 0, 3) };
            settings.Children.Add(tools);
            tools.Children.Add(ActionButton("检查内置出图", () =>
            {
                bool api = FlowChannels.IsResponsesApi(assetChannelId);
                RunAsset(root, version, analysis, async (current, cancel) => "内置出图 · " +
                    await Task.Run(() => api ? AssetChannels.CheckResponsesApi() : AssetChannels.CheckBuiltin(codexStatus.Path, cancel)),
                    false, localOnly: api, name: "内置出图检测");
            }, false));
            tools.Children.Add(ActionButton("检测外部 CLI", () => RunAsset(root, version, analysis, async (current, cancel) =>
            {
                var detected = await Task.Run(() => AssetChannels.Detect(null, AgentData.Options(), cancel));
                return detected.Count == 0 ? "外部平台 CLI · 无" : "外部平台 CLI · 已检测 " + detected.Count + " 家";
            }, false, true, name: "外部渠道检测"), false));
            tools.Children.Add(ActionButton("添加外部 CLI", () => EditAssetChannel(root, null), false));
            tools.Children.Add(ActionButton("编辑所选 CLI", delegate
            {
                var channel = chosen();
                if (channel == null || FlowChannels.IsResponsesApi(channel)) { SetStatus("请先选择一个外部 CLI。"); return; }
                EditAssetChannel(root, channel);
            }, false));
            var references = new WrapPanel();
            settings.Children.Add(references);
            references.Children.Add(ActionButton(work.ReferencePath == null ? "选择参考图" : "更换参考图", delegate
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "参考图|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true };
                if (picker.ShowDialog(Window) == true)
                    UpdateAsset(root, version, analysis, current => AssetProduction.SetReference(root, current, picker.FileName), "项目参考图");
            }, false));
            if (work.ReferencePath != null)
                references.Children.Add(ActionButton("清除参考图", () => UpdateAsset(root, version, analysis, current =>
                {
                    current.ReferencePath = null;
                    current.ReferenceDigest = null;
                }, "项目参考图"), false));
        }
    }
}
