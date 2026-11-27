using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private Button SkillPicker(string projectPath, string operation, string label, Action<SkillReference> changed = null)
        {
            SkillReference current = null;
            TryAction(delegate { current = LocalSkills.Choice(projectPath, operation); });
            if (FlowSkills.Managed(projectPath, operation))
                return ActionButton("本阶段 Skill · " + (current == null ? "内置流程" : current.Name) + "（自动分配）", () => TryAction(delegate {
                    var panel = new StackPanel();
                    var dialog = FormDialog("本阶段 Skill · " + label, panel);
                    var plan = AgentData.Read<FlowSkillPlan>(FlowSkills.PlanPath(projectPath));
                    string stage = FlowSkills.StageForOperation(operation);
                    var route = plan.routes.FirstOrDefault(r => r.stage == stage);
                    panel.Children.Add(Title(current == null ? "内置流程" : current.Name, 18));
                    var config = FlowCreation.Config(projectPath);
                    if (config != null && config.IsV2Builtin && route != null)
                    {
                        panel.Children.Add(Muted(stage == "final-prompt" ? "建项时选定的最终提示词 Skill" : "内置上游工作流（只读）", 13, 4));
                        if (stage == "final-prompt" && current != null)
                        {
                            var selected = FlowSkills.Resolve(projectPath, current);
                            panel.Children.Add(new ScrollViewer { Content=StudioText(selected.Body,13,4), MaxHeight=260,
                                VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
                        }
                    }
                    if (route != null) panel.Children.Add(StudioText(route.guide, 14, 8));
                    if (plan.Decisions != null)
                        foreach (var d in plan.Decisions.Where(d => d.Stage == stage))
                            panel.Children.Add(StudioText(d.Point + " · " + plan.Skills[d.Selected - 1].Name + " · " + d.Reason, 14, 8));
                    panel.Children.Add(ActionButton("前往项目总览", () => dialog.DialogResult = true, false));
                    panel.Children.Add(ActionButton("关闭", () => dialog.Close(), false));
                    if (dialog.ShowDialog() == true) ShowOverview();
                }), false);
            Button choose = null;
            choose = ActionButton(SkillLabel(label, current), delegate
            {
                if (sendingCodexTask) { SetStatus("当前 Codex 任务完成后可更换 Skill。"); return; }
                var selected = ShowSkillPicker(projectPath, operation, label);
                choose.Content = SkillLabel(label, selected);
                if (changed != null) changed(selected);
            }, false);
            AutomationProperties.SetName(choose, "选择 Skill · " + label);
            choose.ToolTip = "可勾选多个本机 Skill";
            return choose;
        }

        private static string SkillLabel(string label, SkillReference selected)
        {
            if (selected == null) return label + " Skill";
            return label + " Skill · " + (selected.Members == null ? AgentData.Short(selected.Name, 24) : selected.Members.Count + " 项");
        }

        private FrameworkElement SkillControls(string project, string operation, string label)
        {
            var controls = new WrapPanel();
            var repair = ActionButton("检查组合冲突", () => RepairSkills(project, operation, label), false);
            Action<SkillReference> refresh = selected => repair.Visibility = selected != null && selected.Members != null
                && selected.Members.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            repair.Visibility = Visibility.Collapsed;
            TryAction(() => refresh(LocalSkills.Choice(project, operation)));
            controls.Children.Add(SkillPicker(project, operation, label, refresh));
            controls.Children.Add(repair);
            return controls;
        }

        private bool ResolveSkill(string projectPath, string operation, out SelectedSkill skill)
        {
            SelectedSkill resolved = null;
            bool ok = TryAction(delegate { resolved = LocalSkills.Resolve(projectPath, LocalSkills.Choice(projectPath, operation)); });
            skill = resolved;
            return ok;
        }

        private SkillReference ShowSkillPicker(string projectPath, string operation, string label)
        {
            SkillReference current = null;
            var catalog = LocalSkills.List(projectPath);
            TryAction(delegate { current = LocalSkills.Choice(projectPath, operation); });
            var picked = new HashSet<string>(LocalSkills.Members(current).Select(s => s.Id));
            var form = StyleDialog(new Window
            {
                Owner = agentWindow != null && agentWindow.IsActive ? agentWindow : Window,
                Title = "选择 Skill · " + label,
                FontSize = Window.FontSize, Background = Brush("Panel"),
                Width = 690, Height = 545, MinWidth = 540, MinHeight = 455, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            });
            var area = new DockPanel { Margin = new Thickness(20) };
            var head = new StackPanel();
            DockPanel.SetDock(head, Dock.Top);
            area.Children.Add(head);
            var heading = new DockPanel();
            head.Children.Add(heading);
            var count = Muted("", 13, 5);
            head.Children.Add(count);
            var search = CodexTextBox("按名称或用途查找", "", 34);
            search.Margin = new Thickness(0, 8, 0, 10);
            head.Children.Add(search);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            area.Children.Add(footer);
            var list = new ListBox { Background = Brush("Input"), Foreground = Window.Foreground, BorderBrush = Brush("Line") };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(list, "本机 Skill 列表");
            area.Children.Add(list);

            Action status = delegate
            {
                var missing = picked.Where(id => !catalog.Any(s => s.Id == id)).Count();
                count.Text = label + " · 已勾选 " + picked.Count + " 项" + (missing == 0 ? "" : " · " + missing + " 项已找不到");
            };
            Action filter = delegate
            {
                string query = search.Text.Trim();
                list.Items.Clear();
                foreach (var entry in catalog.Where(s => (s.Name + " " + s.Description).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var text = new StackPanel();
                    text.Children.Add(Title(entry.Name + " · " + entry.Location, 14));
                    if (!String.IsNullOrWhiteSpace(entry.Description)) text.Children.Add(ProjectAnalysisParagraph(AgentData.Short(entry.Description, 125), 12));
                    var check = new CheckBox { Content = text, IsChecked = picked.Contains(entry.Id),
                        Foreground = Window.Foreground, Margin = new Thickness(5, 3, 5, 3) };
                    AutomationProperties.SetName(check, entry.Name);
                    check.Checked += delegate { picked.Add(entry.Id); status(); };
                    check.Unchecked += delegate { picked.Remove(entry.Id); status(); };
                    list.Items.Add(check);
                }
                status();
            };
            var refresh = ActionButton("刷新列表", delegate
            {
                if (TryAction(delegate { catalog = LocalSkills.List(projectPath); })) filter();
            }, false);
            DockPanel.SetDock(refresh, Dock.Right);
            heading.Children.Add(refresh);
            heading.Children.Add(Title("选择本机 Skill", 24));
            footer.Children.Add(ActionButton("清空选择", delegate { picked.Clear(); filter(); }, false));
            var cancel = ActionButton("取消", delegate { form.DialogResult = false; }, false);
            cancel.IsCancel = true;
            footer.Children.Add(cancel);
            footer.Children.Add(ActionButton("使用所选 Skill", delegate
            {
                var available = catalog.Where(s => picked.Contains(s.Id)).ToList();
                if (available.Count != picked.Count) { count.Text = "有 Skill 已找不到，请清空后重新选择。"; return; }
                var saved = picked.SetEquals(LocalSkills.Members(current).Select(s => s.Id)) ? current : LocalSkills.Selection(available);
                if (TryAction(delegate { LocalSkills.SaveSelection(projectPath, operation, saved); }))
                {
                    current = saved;
                    form.DialogResult = true;
                }
            }, true));
            search.TextChanged += delegate { filter(); };
            filter();
            form.Content = new Border { Background = Brush("Panel"), Child = area };
            form.Loaded += delegate { search.Focus(); };
            form.ShowDialog();
            return current;
        }

        private async void RepairSkills(string project, string operation, string label)
        {
            if (sendingCodexTask || systemBusy) return;
            SelectedSkill skill;
            if (!ResolveSkill(project, operation, out skill)) return;
            if (skill == null || skill.Parts == null) { SetStatus("先为「" + label + "」选择至少两个 Skill。"); return; }
            if (codexStatus == null || codexStatus.State != CodexState.SignedIn)
            {
                SetStatus(CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先检测并登录本机 Codex。"); RefreshCodex(); return;
            }
            string exe = codexStatus.Path;
            string model = AgentData.Options().Model;
            agentCancel = new CancellationTokenSource();
            var cancel = agentCancel.Token;
            sendingCodexTask = true;
            agentSaveFailed = false;
            SetStatus("正在检查 Skill 冲突…");
            string outcome = "";
            try
            {
                string conflicts = await Task.Run(() => SkillCompatibility.Describe(skill,
                    SkillCompatibility.Check(exe, model, skill, cancel)));
                if (conflicts.Length == 0) outcome = "未发现所选 Skill 的规则冲突。";
                else
                {
                    var repair = await Task.Run(() => SkillCompatibility.Repair(exe, model, skill, conflicts, cancel));
                    var proposed = new SkillReference { Name = skill.Reference.Name, Members = skill.Reference.Members,
                        RepairAgainst = skill.Stamp, Resolution = repair.resolution };
                    var combined = LocalSkills.Resolve(project, proposed);
                    await Task.Run(() => SkillCompatibility.Ensure(exe, model, combined, cancel));
                    cancel.ThrowIfCancellationRequested();
                    if (exitAfterCodexTask) return;
                    string answer;
                    if (Prompt("预览 Skill 冲突修复", conflicts + "\n\n更改说明\n" + repair.changes
                        + "\n\n本阶段协调规则\n" + repair.resolution, "应用到本阶段", "取消", false, out answer))
                    {
                        var current = LocalSkills.Resolve(project, LocalSkills.Choice(project, operation));
                        if (current == null || current.Reference.Digest != skill.Reference.Digest)
                            throw new InvalidOperationException("Skill 选择已改变，请重新检查。");
                        LocalSkills.SaveSelection(project, operation, proposed);
                        outcome = "已应用并复检通过 · " + AgentData.Short(repair.changes, 350);
                    }
                    else outcome = "已取消应用 Skill 修复。";
                }
            }
            catch (OperationCanceledException) { outcome = "Skill 检查已停止。"; }
            catch (Exception e) { outcome = AgentData.Short(e.Message, 600); }
            finally
            {
                agentCancel.Dispose();
                agentCancel = null;
                sendingCodexTask = false;
                UpdateCodexDisplay();
                if (exitAfterCodexTask)
                {
                    exitAfterCodexTask = false;
                    FinishExit();
                }
                else SetStatus(outcome);
            }
        }
    }
}