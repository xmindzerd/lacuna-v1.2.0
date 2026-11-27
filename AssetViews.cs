using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private string assetChannelId = "builtin";

        private Border AssetTools(string root, ScriptDocument script, AnalysisVersion version, AnalysisResult analysis,
            AssetItem item, Func<AssetChannel> chosen, AssetWork displayedWork, Panel inspector)
        {
            var card = new StackPanel { Margin = new Thickness(14) };
            var current = item.Active;
            bool needsPrompt = !AssetProduction.PromptReady(item);
            bool conflict = !String.IsNullOrWhiteSpace(item.Conflict);
            bool hasImage = current != null && current.Path != null;
            card.Children.Add(Title(item.Source.category + " · " +
                (conflict ? "提示词待核实" :
                    current == null ? (needsPrompt ? "待生成提示词" : "待出图") : current.State), 16));
            card.Children.Add(ProjectAnalysisParagraph(item.Source.reason, 13));
            if (hasImage)
            {
                card.Children.Add(ProjectAnalysisParagraph(current.Channel +
                    (current.PromptSkill == null ? "" : " · 提示词 Skill " + current.PromptSkill.Name) +
                    (current.ImageSkill == null ? "" : " · 生图 Skill " + current.ImageSkill.Name) +
                    (current.DurationMs <= 0 ? "" : " · 用时 " + (current.DurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒")
                    + ImageOutputSettings.SizeStatus(current), 13));
                card.Children.Add(ProjectAnalysisParagraph(current.VisualCheck, 13));
            }
            var buttons = new WrapPanel { IsEnabled = true, Margin = new Thickness(0, 5, 0, 0) };
            bool directSpecs = AssetProduction.UsesDirectSpecs(root);
            string relationship = AssetProduction.StateLabel(AssetProduction.StateParents(displayedWork), item);
            if (relationship != null) inspector.Children.Add(ProjectAnalysisParagraph(relationship, 14));
            if (current != null && current.State == "已退回" && !String.IsNullOrWhiteSpace(current.ReviewNote))
                inspector.Children.Add(ProjectAnalysisParagraph(current.ReviewNote, 13));
            if (needsPrompt && !directSpecs)
            {
                var saved = AssetProduction.CurrentRepair(item, item.PromptStyle ?? displayedWork.Style);
                if (saved != null) AddRepairActions(buttons, root, version, analysis, script, item, saved);
                else buttons.Children.Add(ActionButton("填写制作决定 / Codex 核实",
                    () => ShowAssetPromptRepair(root,version,analysis,script,item,null),true));
            }
            buttons.Children.Add(ActionButton(needsPrompt ? "手动编辑并确认提示词" : "编辑提示词",
                () => ShowAssetPromptEditor(root,version,analysis,item),false));
            buttons.Children.Add(ActionButton(hasImage ? "替换本地图片" : "导入本地图片",
                () => ImportAssetImage(root,version,analysis,item),false));
            if (current == null || current.State == "未提交" || current.State == "已退回" || current.State == "已通过" || current.State == "待审图")
            {
                if (!directSpecs) buttons.Children.Add(ActionButton("准备本项提示词",
                    () => PrepareAssets(root, version, analysis, new[] { item.Id }, assetStyleDraft, true), false));
                if (!needsPrompt)
                    buttons.Children.Add(ActionButton(current == null ? "仅出这张图" : "重新出图",
                        () => GenerateAssets(root, version, analysis, new[] { item.Id }, chosen()), false));
            }
            if (current != null)
            {
                if (!String.IsNullOrWhiteSpace(current.Detail))
                    card.Children.Add(ProjectAnalysisParagraph(current.Detail, 13));

                if (current.State == "待审图")
                {
                    buttons.Children.Add(ActionButton("视觉自检", () => RunAsset(root, version, analysis, async (work, cancel) =>
                    {
                        var entry = work.Items.Single(i => i.Id == item.Id);
                        var attempt = RequireAttempt(entry, current.Id, "待审图");
                        string picture = AssetProduction.Verify(root, attempt);
                        using (AssetUsageLedger.Begin(root, version.Id, AssetUsageLedger.Visual))
                        try
                        {
                            var advice = await Task.Run(() => AssetProduction.CodexText(codexStatus.Path, AgentData.Options().Model,
                                "检查这张实际资产图。资产是：" + entry.Source.name + "。剧本要求：" + entry.Source.reason +
                                "。出图要求：" + attempt.Prompt + "。报告不符和疑点供人工核对，不做审批。", cancel, picture));
                            AssetUsageLedger.Success(1);
                            attempt.VisualCheck = "Codex 自检建议 · " + AgentData.Short(advice, 1000);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { attempt.VisualCheck = "未验证 · " + AgentData.Short(e.Message, 300); }
                        AssetProduction.Save(root, work);
                        return attempt.VisualCheck;
                    }, name: "视觉自检 · " + item.Id), false));
                    buttons.Children.Add(ActionButton("通过此图", () => ReviewAsset(root, version, analysis, item, current, true), !needsPrompt));
                    buttons.Children.Add(ActionButton("退回重做", () => ReviewAsset(root, version, analysis, item, current, false), false));
                }
                if (current.State == "运行中" || current.State == "需核对" || current.State == "已暂停")
                {
                    buttons.Children.Add(ActionButton("核对原任务", () => UpdateAsset(root, version, analysis, work =>
                    {
                        var attempt = RequireAttempt(work.Items.Single(i => i.Id == item.Id), current.Id, current.State);
                        AssetProduction.Recover(root, work, attempt);
                    }, "资产图 · " + item.Id), false));
                    if (current.State == "需核对")
                    {
                        buttons.Children.Add(ActionButton("登记原图", () => RegisterAsset(root, version, analysis, item, current, true), false));
                        buttons.Children.Add(ActionButton("登记原任务失败", () => RegisterAsset(root, version, analysis, item, current, false), false));
                    }
                }
            }
            inspector.Children.Add(buttons);
            var group = (displayedWork.Groups ?? new List<AssetGroup>()).FirstOrDefault(g => g.ids.Contains(item.Id));
            List<AssetAttempt> baseHistory;
            if (group != null && displayedWork.BaseHistory != null && displayedWork.BaseHistory.TryGetValue(group.Key, out baseHistory))
                AddBaseHistory(inspector, root, "查看本组共用基础图历史", baseHistory);
            if (current != null && current.BaseImagePath != null)
            {
                var reference = new StackPanel();
                AddAssetPreview(reference, root, new AssetAttempt { Path = current.BaseImagePath, Digest = current.BaseImageDigest });
                card.Children.Add(new Expander { Header = "查看本次使用的基础图", Content = reference, Foreground = Window.Foreground });
            }
            string actual = current == null || current.Path == null ? null : current.Prompt;
            string visiblePrompt = actual ?? item.Prompt;
            if (!String.IsNullOrWhiteSpace(visiblePrompt))
            {
                card.Children.Add(Title(actual == null ? "待提交的资产提示词" : "本张图的生成提示词", 16));
                card.Children.Add(ProjectAnalysisParagraph(visiblePrompt, 14));
                if (!String.IsNullOrWhiteSpace(item.Assumptions))
                    card.Children.Add(ProjectAnalysisParagraph("待提交设计的补充说明 · " + item.Assumptions, 13));
            }
            if (actual != null && item.Prompt != null && item.Prompt != actual)
                card.Children.Add(new Expander { Header = "查看更新后的待提交提示词", Foreground = Window.Foreground,
                    Content = ProjectAnalysisParagraph(item.Prompt, 13) });
            if (!String.IsNullOrWhiteSpace(item.Conflict))
                card.Children.Add(Notice("提示词待核实 · " + item.Conflict));
            if (!String.IsNullOrWhiteSpace(item.PromptRepairNote))
                card.Children.Add(new Expander { Header = "查看上次 Codex 修复", Foreground = Window.Foreground,
                    Content = ProjectAnalysisParagraph(item.PromptRepairNote, 13) });
            if (!String.IsNullOrWhiteSpace(item.PromptDocument))
                card.Children.Add(new Expander { Header = "查看 Skill 交付原文", Foreground = Window.Foreground,
                    Content = ProjectAnalysisParagraph(item.PromptDocument, 13) });
            if (!String.IsNullOrWhiteSpace(item.PromptOrigin))
                card.Children.Add(ProjectAnalysisParagraph("待提交提示词来源 · " + item.PromptOrigin +
                    (item.PromptSkill == null ? " · 默认规则" : " · " + item.PromptSkill.Name), 13));
            if (!String.IsNullOrWhiteSpace(item.Source.direction))
                card.Children.Add(ProjectAnalysisParagraph(item.Source.direction, 13));
            card.Children.Add(Title("剧本原文依据", 16));
            var lines = (actual == null ? item.PromptLines : current.PromptLines) ?? item.Source.evidence;
            foreach (int line in lines)
            {
                if (line <= 0 || line > script.Lines.Length) continue;
                string id = item.Id;
                var link = ActionButton("查看 " + script.LineLocation(line, line),
                    () => ShowScriptReader("返回资产详情", () => ShowAssetPage(id), line, script), false);
                link.HorizontalAlignment = HorizontalAlignment.Left;
                card.Children.Add(link);
                card.Children.Add(ProjectAnalysisParagraph(script.Lines[line - 1], 13));
            }
            if (item.Attempts.Any(a => a.Id != item.ActiveId))
            {
                var previous = new StackPanel();
                foreach (var older in item.Attempts.Where(a => a.Id != item.ActiveId).Reverse())
                {
                    previous.Children.Add(ProjectAnalysisParagraph(older.CreatedUtc + " · " + older.State + " · " +
                        older.Channel + "\n" + older.Prompt + "\n" + older.ReviewNote, 13));
                    if (older.Path != null) AddAssetPreview(previous, root, older);
                }
                card.Children.Add(new Expander { Header = "查看历次制作", Content = previous, Foreground = Window.Foreground });
            }
            return Panel(card);
        }

        private static AssetAttempt RequireAttempt(AssetItem item, string id, string state)
        {
            var attempt = item.Active;
            if (attempt == null || attempt.Id != id || attempt.State != state)
                throw new InvalidOperationException("该资产状态已变化，请打开最新记录后重试。");
            return attempt;
        }

        private void AddAssetPreview(StackPanel panel, string root, AssetAttempt attempt)
        {
            try
            {
                string file = AssetProduction.Verify(root, attempt);
                var image = LoadBitmapFile(file, 420);
                var frame = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = Brush("Line"),
                    BorderThickness = new Thickness(1),
                    Background = Brush("Input"),
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 8),
                    Child = new Image { Source = image, MaxHeight = 240, MaxWidth = 400 }
                };
                panel.Children.Add(frame);
                string label = attempt.Width > 0 && attempt.Height > 0
                    ? "打开原图 · " + attempt.Width + " × " + attempt.Height : "打开原图";
                var original = ActionButton(label,
                    () => TryAction(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        AssetProduction.Verify(root, attempt)) { UseShellExecute = true })), false);
                original.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(original);
                var folder = ActionButton("打开所属文件夹", () => ShowContainingFolder(file), false);
                folder.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(folder);
            }
            catch (Exception e) { panel.Children.Add(ProjectAnalysisParagraph(e.Message, 13)); }
        }

        private void AddBaseHistory(Panel parent, string root, string title, List<AssetAttempt> attempts)
        {
            if (attempts == null || attempts.Count == 0) return;
            var list = new StackPanel();
            var history = new Expander { Header = title + "（" + attempts.Count + " 张）", Content = list, Foreground = Window.Foreground };
            history.Expanded += delegate {
                if (list.Children.Count > 0) return;
                foreach (var attempt in attempts.AsEnumerable().Reverse())
                {
                    list.Children.Add(ProjectAnalysisParagraph(attempt.State + " · " + (attempt.ReviewedUtc ?? attempt.CreatedUtc) +
                        "\n" + (attempt.ReviewNote ?? attempt.Detail), 13));
                    if (!String.IsNullOrEmpty(attempt.Path)) AddAssetPreview(list, root, attempt);
                    else list.Children.Add(Muted("此尝试未记录图片路径。", 13, 4));
                }
            };
            parent.Children.Add(history);
        }

        private void RejectBaseDialog(string root, AnalysisVersion version, AnalysisResult analysis, string lane, string batchId, AssetImageJob basis)
        {
            var form = new StackPanel();
            var dialog = FormDialog("退回基础图 · " + basis.Name, form);
            var tweak = new RadioButton { Content = "按修改意见微调基础图", IsChecked = true };
            var replace = new RadioButton { Content = "使用全新提示词重做基础图" };
            form.Children.Add(tweak);
            form.Children.Add(replace);
            var edits = new StackPanel();
            var full = new StackPanel { Visibility = Visibility.Collapsed };
            form.Children.Add(edits);
            form.Children.Add(full);
            var note = AgentField(edits, "修改意见", "", 82);
            var prompt = AgentField(full, "全新提示词", basis.Prompt, 138);
            var message = ProjectAnalysisParagraph("", 13);
            form.Children.Add(message);
            Action refresh = delegate {
                edits.Visibility = replace.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
                full.Visibility = replace.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                message.Text = "";
            };
            tweak.Checked += delegate { refresh(); };
            replace.Checked += delegate { refresh(); };
            form.Children.Add(ActionButton("确认退回", delegate {
                bool redo = replace.IsChecked == true;
                string value = redo ? prompt.Text : note.Text;
                if (String.IsNullOrWhiteSpace(value))
                {
                    message.Text = redo ? "请填写完整的新提示词。" : "请填写修改意见。";
                    (redo ? prompt : note).Focus();
                    return;
                }
                if (UpdateAsset(root, version, analysis, w => AssetProduction.ReviewBase(root, w, batchId, basis.Attempt.Id,
                    false, redo, value), lane)) dialog.Close();
            }, true));
            form.Children.Add(ActionButton("取消", () => dialog.Close(), false));
            dialog.ShowDialog();
        }

        private bool UpdateAsset(string root, AnalysisVersion version, AnalysisResult analysis, Action<AssetWork> update, string resource = null)
        {
            bool saved = TryAction(delegate
            {
                using (resource == "交付" ? (IDisposable)AnalysisData.Lock(root) : new ProjectTaskLease(root, resource ?? "出图"))
                {
                    AssetProduction.RequireCurrent(root, version.Id);
                    var work = AssetProduction.Load(root, version, analysis);
                    update(work);
                    AssetProduction.Save(root, work);
                    if (SameProject(root, projectFolder) && !AssetRunning(root, "出图"))
                    {
                        var pendingBatch = work.Batches().Select(p => p.Value).FirstOrDefault(b => b.NeedsAction);
                        assetProgressText = pendingBatch == null ? null : AssetProduction.PendingSummary(pendingBatch);
                        assetProgressProject = root;
                    }
                }
            });
            if (saved && SameProject(root, projectFolder)) {
                if (blockedListMarker != null && content.Children.Contains(blockedListMarker)) ShowBlockedAssetPrompts(root,version,analysis);
                else ShowAssetPage(assetDetailId);
            }
            return saved;
        }

        private void ReviewAsset(string root, AnalysisVersion version, AnalysisResult analysis,
            AssetItem item, AssetAttempt expected, bool pass)
        {
            string comment;
            if (pass)
            {
                if (!Prompt("通过此图",item.Source.name,"通过","取消",false,out comment)) return;
                UpdateAsset(root,version,analysis,work => {
                    var attempt=RequireAttempt(work.Items.Single(i=>i.Id==item.Id),expected.Id,"待审图");
                    AssetProduction.Verify(root,attempt); attempt.State="已通过"; attempt.ReviewedUtc=AnalysisData.Now();
                },"资产图 · "+item.Id);
                return;
            }
            var panel=new StackPanel();
            var dialog=FormDialog("退回资产图 · "+item.Source.name,panel);
            var tweak=new RadioButton {Content="按修改意见微调",IsChecked=true};
            var replace=new RadioButton {Content="使用全新提示词彻底重做"};
            panel.Children.Add(tweak);panel.Children.Add(replace);
            var note=AgentField(panel,"修改意见","",80);
            var full=new StackPanel();panel.Children.Add(full);
            var prompt=AgentField(full,"全新提示词",item.Prompt ?? expected.Prompt,130);
            var currentWork = AssetProduction.Load(root, version, analysis);
            int children = AssetProduction.ChildStates(currentWork, item.Id).Count;
            if (children != 0) panel.Children.Add(ProjectAnalysisParagraph("退回基准态将同步作废或提醒重做下挂的 " + children + " 个状态变体。", 14));
            var group=(currentWork.Groups ?? new List<AssetGroup>()).FirstOrDefault(g=>g.ids.Contains(item.Id));
            var clear=new CheckBox {Content="同时作废本组共用的旧基础图（更换画风或构图时勾选）",IsChecked=true};
            if(group!=null)full.Children.Add(clear);
            full.Visibility=Visibility.Collapsed;
            replace.Checked+=delegate {full.Visibility=Visibility.Visible;note.IsEnabled=false;clear.IsChecked=true;};
            tweak.Checked+=delegate {full.Visibility=Visibility.Collapsed;note.IsEnabled=true;};
            panel.Children.Add(ActionButton("确认退回",delegate {
                bool redo=replace.IsChecked==true;
                if(UpdateAsset(root,version,analysis,w=>AssetProduction.RejectImage(w,item.Id,expected.Id,
                    redo,redo ? prompt.Text : note.Text,redo && group!=null && clear.IsChecked==true),"交付")) dialog.Close();
            },true));
            panel.Children.Add(ActionButton("取消",()=>dialog.Close(),false));
            dialog.ShowDialog();
        }

        private void ImportAssetImage(string root, AnalysisVersion version, AnalysisResult analysis,AssetItem item)
        {
            var picker=new OpenFileDialog {Filter="资产图|*.png;*.jpg;*.jpeg;*.webp",CheckFileExists=true};
            if(picker.ShowDialog(Window)!=true)return;
            var form=new StackPanel();
            var dialog=FormDialog("导入资产图 · "+item.Source.name,form);
            form.Children.Add(StudioText(Path.GetFileName(picker.FileName),15,4));
            Action<bool> save=approved=>{
                if(UpdateAsset(root,version,analysis,w=>AssetProduction.ImportLocal(root,w,item.Id,item.ActiveId,picker.FileName,approved),"交付"))dialog.Close();
            };
            form.Children.Add(ActionButton("导入并设为已通过",()=>save(true),true));
            form.Children.Add(ActionButton("导入为待审图",()=>save(false),false));
            form.Children.Add(ActionButton("取消",()=>dialog.Close(),false));
            dialog.ShowDialog();
        }

        private void RegisterAsset(string root, AnalysisVersion version, AnalysisResult analysis,
            AssetItem item, AssetAttempt expected, bool retrieved)
        {
            string evidence;
            if (!Prompt("登记原渠道核对结果", "在原渠道核实「" + item.Source.name + "」本次任务与结果。",
                "保存核对", "取消", true, out evidence, "核对依据")) return;
            string file = null;
            if (retrieved)
            {
                var picker = new OpenFileDialog { Filter = "资产图|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true };
                if (picker.ShowDialog(Window) != true) return;
                file = picker.FileName;
            }
            UpdateAsset(root, version, analysis, work =>
            {
                var attempt = RequireAttempt(work.Items.Single(i => i.Id == item.Id), expected.Id, "需核对");
                if (retrieved) AssetProduction.Attach(root, work, attempt, file);
                else attempt.State = "未提交";
                attempt.Detail = "人工核对 · " + evidence;
            }, "资产图 · " + item.Id);
        }

        private void ShowAssetPromptEditor(string root, AnalysisVersion version, AnalysisResult analysis, AssetItem item)
        {
            var form = new StackPanel();
            var dialog = FormDialog("编辑资产提示词 · "+item.Source.name,form);
            if (!String.IsNullOrWhiteSpace(item.Conflict)) form.Children.Add(Notice("需核实 · "+item.Conflict));
            var input = AgentField(form, "出图提示词", item.Prompt, 150);
            var resolved = new CheckBox { Content = "我已核实并处理以上冲突", Margin = new Thickness(0, 8, 0, 8) };
            if (!String.IsNullOrWhiteSpace(item.Conflict)) form.Children.Add(resolved);
            form.Children.Add(ActionButton("保存提示词", () => { if(UpdateAsset(root, version, analysis, work =>
            {
                var current = work.Items.Single(i => i.Id == item.Id);
                if (current.Prompt != item.Prompt || current.Conflict != item.Conflict)
                    throw new InvalidOperationException("该提示词已变化，请打开最新记录。");
                if (String.IsNullOrWhiteSpace(input.Text))
                    throw new InvalidOperationException("请填写出图提示词。");
                current.Prompt = input.Text.Trim();
                current.PromptOrigin = "人工编辑";
                if (resolved.IsChecked == true) current.Conflict = null;
                current.PendingRepair = null;
            }, "编辑提示词 · " + item.Id)) dialog.Close(); }, true));
            form.Children.Add(ActionButton("取消",()=>dialog.Close(),false));
            dialog.ShowDialog();
        }

        private UIElement blockedListMarker;

        private void StartAssetPromptDiagnosis(string root, AnalysisVersion version, AnalysisResult analysis, string id)
        {
            StartPromptChecks(root, version, analysis, new[] { id });
        }

        private void StartPromptChecks(string root, AnalysisVersion version, AnalysisResult analysis, string[] ids, string decision = null)
        {
            if (!SameProject(root, projectFolder) || ids.Length == 0) return;
            string lane = ids.Length == 1 ? "核实提示词 · " + ids[0] : "批量核实提示词";
            RunAsset(root, version, analysis, async (work, cancel) =>
            {
                var script = AnalysisData.CurrentScript(root);
                string exe = codexStatus.Path, model = AgentData.Options().Model;
                var items = work.Items.Where(i => ids.Contains(i.Id) && !AssetProduction.PromptReady(i) &&
                    (decision != null || AssetProduction.CurrentRepair(i, i.PromptStyle ?? work.Style) == null)).ToList();
                var errors = new List<string>();
                int finished = 0;
                using (AssetUsageLedger.Begin(root, version.Id, AssetUsageLedger.Verify))
                using (var limit = new SemaphoreSlim(4))
                {
                    await Task.WhenAll(items.Select(async item =>
                    {
                        await limit.WaitAsync(cancel);
                        try
                        {
                            using (new ProjectTaskLease(root, "核实资产 " + item.Id))
                            {
                                await DiagnoseAndSave(root, version, analysis, script, work.Style, item, exe, model, decision, cancel);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { errors.Add(item.Source.name + " · " + AgentData.Short(e.GetBaseException().Message, 120)); }
                        finally
                        {
                            limit.Release();
                            SetAssetProgress(root, "已检查 " + (++finished) + " / " + items.Count, finished, items.Count, lane);
                        }
                    }));
                }
                return errors.Count == 0 ? "核实完成，可查看并采纳各项方案。" :
                    "已完成 " + (finished - errors.Count) + " 项；可重试 · " + String.Join(" / ", errors);
            }, name: lane);
        }

        private async Task DiagnoseAndSave(string root, AnalysisVersion version, AnalysisResult analysis, ScriptDocument script,
            string projectStyle, AssetItem item, string exe, string model, string decision, CancellationToken cancel)
        {
            string stamp = AssetProduction.PromptRevision(item), style = item.PromptStyle ?? projectStyle;
            var plan = await Task.Run(() => AssetProduction.DiagnosePrompt(exe, model, script, item, style, cancel, decision));
            cancel.ThrowIfCancellationRequested();
            var latest = AssetProduction.Load(root, version, analysis);
            var current = latest.Items.Single(i => i.Id == item.Id);
            if (AssetProduction.PromptRevision(current) != stamp || (current.PromptStyle ?? latest.Style) != style)
                throw new InvalidOperationException("提示词或画风已更新，请重新核实。");
            plan.Revision = stamp; plan.Style = style;
            current.PendingRepair = plan;
            AssetProduction.Save(root, latest);
        }

        private Button AutoReviewButton(string root, AnalysisVersion version, AnalysisResult analysis, IList<AssetItem> items, bool navigate)
        {
            string label = AssetUsageLedger.AutoReview + " · " + items.Count + " 项";
            return ActionButton(label, delegate {
                if (navigate) ShowBlockedAssetPrompts(root, version, analysis);
                var form = new StackPanel();
                var dialog = FormDialog(label, form);
                form.Children.Add(ProjectAnalysisParagraph("Codex 将结合剧本、就绪资产和全部待核实项，统一画风、核对外观并修订提示词。只审核文字，完成后可查看每项决定。", 14));
                var preference = AgentField(form, "补充全局偏好（可选）", "", 65);
                var start = ActionButton("开始分析并自动决定", delegate {
                    string value = preference.Text.Trim();
                    dialog.Close();
                    RunAsset(root, version, analysis, async (work, cancel) => {
                        var script = AnalysisData.CurrentScript(root);
                        string exe = codexStatus.Path, model = AgentData.Options().Model;
                        var summary = await Task.Run(() => AssetProduction.AutoReviewBlocked(root, version, analysis, script,
                            exe, model, value, cancel, message => Window.Dispatcher.BeginInvoke(new Action(() =>
                                SetAssetProgress(root, message, 0, 0, AssetUsageLedger.AutoReview)))));
                        return summary == null ? "所有提示词均已就绪。" : AutoReviewCounts(summary);
                    }, name: AssetUsageLedger.AutoReview);
                }, true);
                start.IsDefault = true;
                form.Children.Add(start);
                form.Children.Add(ActionButton("取消", () => dialog.Close(), false));
                dialog.ShowDialog();
            }, true);
        }

        private static string AutoReviewCounts(AssetAutoReviewSummary summary)
        {
            var entries = summary.decisions;
            return "本次自动审核已保存 " + entries.Count + " / " + summary.TargetCount + " 项 · 采纳现稿 " +
                entries.Count(d => d.action == "adopt_current") + " · 修订定稿 " + entries.Count(d => d.action == "rewrite") +
                " · 需人工 " + entries.Count(d => d.action == "needs_human");
        }

        private void ShowBlockedAssetPrompts(string root, AnalysisVersion version, AnalysisResult analysis)
        {
            var work = AssetProduction.Load(root, version, analysis);
            var items = work.Items.Where(i => !AssetProduction.PromptReady(i)).ToList();
            BeginPage(1, "处理出图前的问题", items.Count == 0 ? "所有提示词均已就绪" : "有 " + items.Count + " 项提示词需要处理", 2);
            AddPageBack("返回资产列表", ShowAssetHandoff);
            var bar = new WrapPanel();
            if (items.Count > 0) bar.Children.Add(AutoReviewButton(root, version, analysis, items, false));
            if (project.Automatic) bar.Children.Add(ActionButton(items.Count == 0 ? "核实完成，继续制作" : "返回项目总览",()=>{ShowOverview(); if(items.Count==0)StartFlow(root,null);},items.Count==0));
            else if (items.Count == 0) bar.Children.Add(ActionButton("返回资产列表开始出图", ShowAssetHandoff, true));
            blockedListMarker = bar;
            bar.Children.Add(ReviewAllButton(ReviewScope.Assets, () => ShowBlockedAssetPrompts(root, version, analysis)));
            content.Children.Add(bar);
            AddAssetProgress(root);
            var script = AnalysisData.CurrentScript(root);
            var summary = work.LastAutoReview;
            if (summary != null && summary.decisions != null)
            {
                content.Children.Add(Notice(AutoReviewCounts(summary)));
                var details = new StackPanel();
                details.Children.Add(ProjectAnalysisParagraph("统一画风 · " + summary.unifiedStyle + "\n" + summary.styleNote, 14));
                foreach (var d in summary.decisions)
                {
                    var asset = work.Items.FirstOrDefault(i => i.Id == d.id);
                    if (asset == null) continue;
                    string action = d.action == "adopt_current" ? "采纳现稿" : d.action == "rewrite" ? "修订定稿" : "需人工决定";
                    details.Children.Add(ActionButton(asset.Source.name + " · " + action, () => ShowAssetPage(asset.Id), false));
                    details.Children.Add(ProjectAnalysisParagraph(d.reason + "\n" + d.guidance + "\n剧本行 · " + String.Join("、", d.lines), 13));
                }
                content.Children.Add(new Expander { Header = "查看本次 Codex 自动审核摘要", Content = details, Foreground = Window.Foreground });
            }
            var pending = items.Where(i => AssetProduction.CurrentRepair(i, i.PromptStyle ?? work.Style) == null).ToList();
            if (pending.Count > 0)
                bar.Children.Add(ActionButton("一键 Codex 核实 · " + pending.Count + " 项",
                    () => StartPromptChecks(root, version, analysis, pending.Select(i => i.Id).ToArray()), true));
            foreach (var item in items)
            {
                var row = new StackPanel { Margin = new Thickness(14, 8, 14, 9) };
                row.Children.Add(Title(item.Source.name, 16));
                row.Children.Add(ProjectAnalysisParagraph(String.IsNullOrWhiteSpace(item.Conflict) ? "缺少出图提示词。" : item.Conflict, 13));
                var actions = new WrapPanel(); row.Children.Add(actions);
                var plan = AssetProduction.CurrentRepair(item, item.PromptStyle ?? work.Style);
                actions.Children.Add(ActionButton("手动编辑并确认提示词",()=>ShowAssetPromptEditor(root,version,analysis,item),false));
                if (plan == null)
                    actions.Children.Add(ActionButton("填写制作决定 / Codex 核实", () => ShowAssetPromptRepair(root,version,analysis,script,item,null),false));
                else
                {
                    row.Children.Add(ProjectAnalysisParagraph(plan.changes, 14));
                    AddRepairActions(actions, root, version, analysis, script, item, plan);
                }
                content.Children.Add(Panel(row));
            }
        }

        private void AddRepairActions(Panel actions, string root, AnalysisVersion version, AnalysisResult analysis,
            ScriptDocument script, AssetItem item, AssetPromptRepair plan)
        {
            if (plan.canApply) actions.Children.Add(ActionButton("采纳修复方案", () => AdoptRepair(root, version, analysis, item.Id, plan), true));
            actions.Children.Add(ActionButton("查看并补充决定",
                () => ShowAssetPromptRepair(root, version, analysis, script, item, plan), !plan.canApply));
        }

        private void AdoptRepair(string root, AnalysisVersion version, AnalysisResult analysis, string id, AssetPromptRepair plan)
        {
            if (!SameProject(root, projectFolder)) return;
            if (!UpdateAsset(root, version, analysis, work =>
            {
                var item = work.Items.Single(i => i.Id == id);
                var current = AssetProduction.CurrentRepair(item, item.PromptStyle ?? work.Style);
                if (current == null || AssetProduction.StableJson(current) != AssetProduction.StableJson(plan))
                    throw new InvalidOperationException("该方案已变化，请查看最新核实结果。");
                AssetProduction.ApplyPromptRepair(item, current.Revision, current, current.Style);
            }, "编辑提示词 · " + id)) return;

            SetStatus("已采纳修复方案，提示词已就绪。");
        }

        private void ShowAssetPromptRepair(string root, AnalysisVersion version, AnalysisResult analysis,
            ScriptDocument script, AssetItem item, AssetPromptRepair plan)
        {
            var preview = new StackPanel();
            var dialog=FormDialog("核实提示词 · "+item.Source.name,preview);
            var actions=new WrapPanel();
            bool newCheck = plan == null;
            plan = plan ?? new AssetPromptRepair { cause=item.Conflict ?? "缺少可用的出图提示词。", changes="填写制作决定或请 Codex 对照剧本提出方案。", lines=item.Source.evidence };
            preview.Children.Add(Title("发现的问题", 16));
            preview.Children.Add(ProjectAnalysisParagraph((plan.cause ?? item.Conflict ?? "").Trim(), 14));
            preview.Children.Add(Title(plan.canApply ? "修改方式" : "需要你确认", 16));
            preview.Children.Add(ProjectAnalysisParagraph((plan.changes ?? "").Trim(), 14));
            if (plan.lines != null && plan.lines.Count > 0)
            {
                var evidence = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
                foreach (int line in plan.lines.Where(n=>n>0 && n<=script.Lines.Length).OrderBy(n => n))
                    evidence.Children.Add(ProjectAnalysisParagraph(script.LineLocation(line,line)+" · "+script.Lines[line-1],13));
                preview.Children.Add(evidence);
            }
            if (plan.canApply)
            {
                var prompts = new Grid();
                prompts.ColumnDefinitions.Add(new ColumnDefinition());
                prompts.ColumnDefinitions.Add(new ColumnDefinition());
                var before = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var after = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
                AgentField(before, "当前提示词", item.Prompt ?? "尚未生成", 156).IsReadOnly = true;
                AgentField(after, "修复后的提示词", plan.prompt.Trim(), 156).IsReadOnly = true;
                Grid.SetColumn(after, 1);
                prompts.Children.Add(before);
                prompts.Children.Add(after);
                preview.Children.Add(prompts);

                actions.Children.Add(ActionButton("采纳修复方案", () => { dialog.Close(); AdoptRepair(root, version, analysis, item.Id, plan); }, true));
            }
            var input = AgentField(preview, "你的制作决定", "", 75);
            preview.Children.Add(ActionButton("按我的决定重新核实", delegate
            {
                if (String.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; }
                dialog.Close();
                StartPromptChecks(root, version, analysis, new[] { item.Id }, input.Text.Trim());
            }, !plan.canApply));
            if(newCheck)
                preview.Children.Add(ActionButton("请 Codex 核实",()=>{dialog.Close(); StartAssetPromptDiagnosis(root,version,analysis,item.Id);},false));
            preview.Children.Add(ActionButton("手动编辑并确认提示词", () => { dialog.Close(); ShowAssetPromptEditor(root, version, analysis, item); }, false));
            actions.Children.Add(ActionButton("关闭",()=>dialog.Close(),false));
            preview.Children.Add(actions);
            dialog.ShowDialog();
        }

        private void PrepareAssets(string root, AnalysisVersion version, AnalysisResult analysis, string[] ids, string style, bool single = false)
        {
            string lane = single ? "准备提示词 · " + ids.Single() : "准备提示词";
            SelectedSkill skill;
            if (!ResolveSkill(root, LocalSkills.StageKey(1), out skill)) return;
            RunAsset(root, version, analysis, async (work, cancel) =>
            {
                string digest = skill == null ? null : skill.Reference.Digest;
                bool resume = !single && work.PendingPromptIds != null && work.PendingPromptIds.SequenceEqual(ids);
                if (resume && (work.PromptRequestStyle != style || work.PromptRequestSkill != digest))
                    ids = (work.PromptRequestIds ?? work.PendingPromptIds).ToArray();
                using (var claims = new PromptWriteLeases(root, ids))
                {
                    if (!single)
                    {
                        if (!resume || work.PromptRequestStyle != style || work.PromptRequestSkill != digest) work.PromptRequestIds = ids.ToList();
                        work.PromptRequestStyle = style; work.PromptRequestSkill = digest;
                        work.PendingPromptIds = ids.ToList();
                        AssetProduction.Save(root, work);
                    }
                    var items = work.Items.Where(i => ids.Contains(i.Id)).ToList();
                    if (items.Count == 0) throw new InvalidOperationException("没有待准备的资产。");
                    var script = AnalysisData.CurrentScript(root);
                    SetAssetProgress(root, "并行准备提示词 · 正在核对剧本依据", 0, items.Count, lane);
                    string executable = codexStatus.Path, model = AgentData.Options().Model;
                    string canvas = project.Automatic ? ImageOutputSettings.DefaultAssetRatio : null;
                    Action<List<AssetDraft>> checkpoint = drafts =>
                    {
                        AssetProduction.RequireCurrent(root, version.Id);
                        if (!single) work.Style = style;
                        foreach (var draft in drafts)
                        {
                            var item = items.Single(i => i.Id == draft.id);
                            AssetProduction.StoreDraft(item, draft, skill, style);
                        }
                        var saved = new HashSet<string>(drafts.Select(d => d.id));
                        if (!single && work.PendingPromptIds != null)
                        {
                            work.PendingPromptIds.RemoveAll(saved.Contains);
                            if (work.PendingPromptIds.Count == 0) { work.PendingPromptIds = null; work.PromptRequestIds = null; }
                        }
                        AssetProduction.Save(root, work);
                    };
                    using (AssetUsageLedger.Begin(root, version.Id, AssetUsageLedger.Prompt))
                    await Task.Run(() => AssetProduction.Prepare(executable, model, script, style, analysis, items, skill,
                        cancel, completed => Window.Dispatcher.BeginInvoke(new Action(() =>
                            SetAssetProgress(root, "并行准备提示词 · 已核对并保存", completed, items.Count, lane))), checkpoint, canvas));
                    int blocked = items.Count(i => !AssetProduction.PromptReady(i));
                    return "已保存 " + items.Count + " 项出图提示词" +
                        (blocked == 0 ? "" : " · " + blocked + " 项需要核实，可在资产列表打开修复方案。");
                }
            }, name: lane);
        }

        private async void RunAsset(string root, AnalysisVersion version, AnalysisResult analysis,
            Func<AssetWork, CancellationToken, Task<string>> run, bool requireLogin = true, bool localOnly = false, string name = "出图")
        {
            if (exitAfterCodexTask || AssetRunning(root, name)) { SetStatus(name + "已在进行中。"); return; }
            if (systemBusy && scriptImportCancel == null || scriptImportCancel != null && SameProject(root, scriptImportProject))
            { SetStatus("请等待本次资料变更完成。"); return; }
            if (!localOnly && (codexStatus == null || (!CodexAuthMode.IsApi && codexStatus.Path == null) || ((requireLogin || CodexAuthMode.IsApi) && codexStatus.State != CodexState.SignedIn)))
            { SetStatus(CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先在设置检测本机 Codex 并确认登录。"); RefreshCodex(); return; }
            ProjectTaskLease lease;
            try { lease = new ProjectTaskLease(root, name); }
            catch (IOException) { SetStatus("该项目的同类任务或剧本变更正在进行。"); return; }
            catch (UnauthorizedAccessException e) { SetStatus(e.Message); return; }
            var task = new AssetTask { Root = root, Name = name, Status = "正在准备" };
            string status;
            try
            {
                BeginAssetTask(task);
                if (SameProject(root, projectFolder)) DrawAssetProgress(root);
                AssetProduction.RequireCurrent(root, version.Id);
                status = await run(AssetProduction.Load(root, version, analysis), task.Cancel.Token);
            }
            catch (OperationCanceledException) { status = name + "已停止"; }
            catch (Exception error)
            {
                if (error is IOException || error is UnauthorizedAccessException) exitAfterCodexTask = false;
                status = name + " · " + AgentData.Short(error.GetBaseException().Message, 450);
            }
            finally { EndAssetTask(task); task.Cancel.Dispose(); lease.Dispose(); }
            SetAssetProgress(root, status, task.Done, task.Total, name);
            if (exitAfterCodexTask) { if (assetTasks.Count == 0 && !sendingCodexTask) FinishExit(); return; }
            if (!SameProject(root, projectFolder)) return;
            if (blockedListMarker != null && content.Children.Contains(blockedListMarker))
                ShowBlockedAssetPrompts(root, version, analysis);
            else if (assetProgressPanel != null && content.Children.Contains(assetProgressPanel))
                ShowAssetPage(assetDetailId);
            SetStatus(status);
        }

        private void EditAssetChannel(string root, AssetChannel existing)
        {
            var form = new StackPanel();
            var dialog = FormDialog("配置外部图片 CLI",form);
            var name = AgentField(form, "平台名称", existing == null ? "" : existing.Name);
            var executable = AgentExecutable(form, "平台 CLI 程序", existing == null ? "" : existing.Executable);
            var args = AgentField(form, "出图参数，每行一个，可用 {prompt}、{prompt_file}、{output}、{reference}（实际参考图路径）",
                existing == null ? "" : existing.Arguments, 70);
            var sizes = AgentField(form, "平台支持的输出尺寸，每行一个如 1536x1024。CLI 参数需同时填 {width} 和 {height}",
                existing == null ? "" : existing.OutputSizes, 65);
            var check = AgentField(form, "只读健康检查参数，每行一个。使用能核对登录或服务状态的命令",
                existing == null ? "" : existing.CheckArguments, 65);
            form.Children.Add(ActionButton("保存平台", delegate
            {
                if (!SameProject(root, projectFolder)) return;
                var platform = new AssetChannel { Id = existing == null ? AgentData.Id() : existing.Id,
                    Name = name.Text.Trim(), Executable = executable.Text.Trim(), Arguments = args.Text,
                    CheckArguments = check.Text, OutputSizes = sizes.Text, Status = "待检测" };
                if (TryAction(delegate
                {
                    if (String.IsNullOrWhiteSpace(platform.Name)) throw new InvalidOperationException("请填写平台名称。");
                    AgentMedia.Validate(platform.Options(AgentData.Options()), "图片", "cli");
                    AssetChannels.Store(platform);
                }))
                {
                    assetChannelId = platform.Id;
                    dialog.Close(); ShowAssetPage(assetDetailId);
                }
            }, true));
            form.Children.Add(ActionButton("取消",()=>dialog.Close(),false));
            dialog.ShowDialog();
        }

        private void AddStoryboardAssets()
        {
            if (projectFolder == null) return;
            ScriptDocument source;
            AnalysisLedger ledger;
            if (!ReadProjectAnalysis(out source, out ledger)) return;
            var confirmed = ledger.Confirmed(source);
            if (confirmed == null) { content.Children.Add(Notice("等待确认剧本分析和资产图。")); return; }
            try
            {
                var work = AssetProduction.Load(projectFolder, confirmed, AnalysisData.ReadResult(projectFolder, confirmed));
                var delivery = AssetProduction.ReadDelivery(projectFolder, work);
                if (delivery == null) { content.Children.Add(Notice("等待确认当前资产版本。")); return; }
                content.Children.Add(Notice("已接收资产图 · " + delivery.Items.Count + " 张 · " + delivery.ConfirmedUtc));
                foreach (var item in delivery.Items)
                {
                    var card = new StackPanel { Margin = new Thickness(12) };
                    card.Children.Add(Title(item.Source.category + " · " + item.Source.name, 15));
                    foreach (int line in item.Source.evidence)
                        card.Children.Add(SourceLink(source, line, line, true));
                    AddAssetPreview(card, projectFolder, item.Active);
                    content.Children.Add(Panel(card));
                }
            }
            catch (Exception e) { content.Children.Add(Notice("当前资产交付不可用 · " + e.Message)); }
        }
    }
}
