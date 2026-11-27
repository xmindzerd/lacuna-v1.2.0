using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private async void LoginCodex()
        {
            if (CodexAuthMode.IsApi || installCancel != null || sendingCodexTask || codexStatus == null || codexStatus.Path == null)
            {
                SetStatus("请先检测并选择 Codex 程序。");
                return;
            }
            Process started = null;
            if (!TryAction(delegate
            {
                var start = new ProcessStartInfo
                {
                    FileName = codexStatus.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : codexStatus.Path,
                    Arguments = codexStatus.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        ? "/d /s /v:off /c \"\"%ONE_CLICK_FILM_CODEX%\" login\"" : "login",
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false
                };
                start.EnvironmentVariables["ONE_CLICK_FILM_CODEX"] = codexStatus.Path;
                loginRefresh = true;
                started = Process.Start(start);
                if (started == null) throw new InvalidOperationException("无法打开 Codex 登录窗口。");
                SetStatus("登录完成后切回 Lacuna，软件会重新检测。");
            })) return;
            using (started) await Task.Run(() => started.WaitForExit());
            if (!exiting) RefreshCodex();
        }

        private async void InstallCodex()
        {
            if (CodexAuthMode.IsApi || installCancel != null || sendingCodexTask) return;
            string ignored;
            if (!Prompt("安装 Codex CLI", "将下载并运行 OpenAI 官方 Windows 安装器。安装结束后会重新检测。",
                "安装", "取消", false, out ignored)) return;
            installCancel = new CancellationTokenSource();
            var cancel = installCancel.Token;
            UpdateCodexDisplay();
            SetInstallState("正在启动 OpenAI 官方安装器…");
            try
            {
                string shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                var run = await Task.Run(() => CodexConnection.Run(shell,
                    "-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; Invoke-RestMethod https://chatgpt.com/codex/install.ps1 | Invoke-Expression\"",
                    1200000, onLine: line => Window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (installCancel != null && !String.IsNullOrWhiteSpace(line)) SetInstallState(AgentData.Short(line, 180));
                    })), cancellation: cancel));
                if (run.Code != 0) throw new InvalidOperationException("官方安装器退出码 " + run.Code + "。" + AgentData.Short(run.Error, 260));
                var statusAfter = await Task.Run(() => CodexConnection.Check(null));
                if (statusAfter.Path == null) throw new InvalidOperationException("安装结束后仍未发现 Codex CLI。请重新打开软件再检测。");
                SetInstallState("安装程序已结束 · 检测到 Codex CLI。");
            }
            catch (OperationCanceledException) { SetInstallState("已停止安装进程。可重新检测已安装的版本。"); }
            catch (Exception e) { SetInstallState("安装未完成 · " + AgentData.Short(e.Message, 320)); }
            finally
            {
                installCancel.Dispose();
                installCancel = null;
                if (!exiting) RefreshCodex();
            }
        }

        private void SetInstallState(string text)
        {
            installState = text;
            if (installStatus != null) installStatus.Text = text;
            SetStatus(text);
        }

        private void AddModelPicker(StackPanel panel)
        {
            bool api = CodexAuthMode.IsApi;
            panel.Children.Add(Muted(api ? "当前全局 API 模型 · " + (String.IsNullOrWhiteSpace(CodexAuthMode.Model)
                ? "未设置（请在下方「Codex 连接方式」填写）" : CodexAuthMode.Model) : "使用的模型", 13, 12));
            var models = AgentRunner.Models();
            ComboBox box = null;
            if (!api)
            {
                if (!String.IsNullOrWhiteSpace(agentOptions.Model) && !models.Any(m => m.Key == agentOptions.Model))
                    models.Add(new AgentModel { Key = agentOptions.Model, Name = "已保存 · " + agentOptions.Model });
                box = new ComboBox { ItemsSource = models, MaxWidth = 430, MinWidth = 245,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
                AutomationProperties.SetName(box, "Codex 模型");
                box.SelectedItem = models.First(m => m.Key == (agentOptions.Model ?? ""));
                panel.Children.Add(box);
            }
            panel.Children.Add(Muted("模型强度", 13, 8));
            var effort = new ComboBox { MinWidth = 245, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0) };
            AutomationProperties.SetName(effort, "Codex 模型强度");
            panel.Children.Add(effort);
            bool loading = false;
            Action reload = delegate
            {
                loading = true;
                effort.Items.Clear();
                effort.Items.Add(new ComboBoxItem { Content = "使用模型默认强度", Tag = "" });
                var selected = api ? models.FirstOrDefault(m => m.Key == CodexAuthMode.Model) : box.SelectedItem as AgentModel;
                var levels = selected == null ? new string[0] : selected.Efforts;
                if (api && levels.Length == 0) levels = new[] { "low", "medium", "high", "xhigh" };
                foreach (var level in levels)
                    effort.Items.Add(new ComboBoxItem { Content = level, Tag = level });
                effort.SelectedItem = effort.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
                    (string)i.Tag == (agentOptions.ReasoningEffort ?? "")) ?? effort.Items[0];
                loading = false;
            };
            Action save = delegate
            {
                var model = api ? null : box.SelectedItem as AgentModel;
                var selected = effort.SelectedItem as ComboBoxItem;
                if ((!api && model == null) || selected == null || loading) return;
                string oldModel = agentOptions.Model, oldEffort = agentOptions.ReasoningEffort;
                if (!api) agentOptions.Model = model.Key;
                agentOptions.ReasoningEffort = (string)selected.Tag;
                if (!TryAction(delegate { AgentData.SaveOptions(agentOptions); }))
                {
                    agentOptions.Model = oldModel;
                    agentOptions.ReasoningEffort = oldEffort;
                    loading = true;
                    if (!api) box.SelectedItem = models.First(m => m.Key == (oldModel ?? ""));
                    reload();
                }
                else { UpdateCodexDisplay(); SetStatus("已保存默认 Codex 模型和强度。"); }
            };
            if (!api) box.SelectionChanged += delegate { if (!loading) { reload(); save(); } };
            effort.SelectionChanged += delegate { save(); };
            reload();
        }

        private void ShowAgentRoutes()
        {
            BeginPage(2, "Codex 制作渠道", "图片与视频");
            AddPageBack("返回 Codex 助手", OpenAgent);
            var layout = new StackPanel { Margin = new Thickness(16) };
            layout.Children.Add(Title("制作渠道", 21));
            layout.Children.Add(Muted("每次制作先确认渠道和预估金额。已确认的外部资产图片由 Lacuna 直接提交。", 13, 5));
            var imageRoute = RoutePicker(layout, "图片渠道", new[] { "builtin", "cli" }, agentOptions.ImageRoute);
            var videoRoute = RoutePicker(layout, "视频渠道", new[] { "cli", "api" }, agentOptions.VideoRoute);
            var imageCli = AgentExecutable(layout, "图片平台 CLI", agentOptions.ImageCli);
            var imageArgs = AgentField(layout, "图片 CLI 参数 · 每行一个", agentOptions.ImageArgs, 58);
            var videoCli = AgentExecutable(layout, "视频平台 CLI", agentOptions.VideoCli);
            var videoArgs = AgentField(layout, "视频 CLI 参数 · 每行一个", agentOptions.VideoArgs, 58);
            layout.Children.Add(Muted("参数变量：{prompt} 为正文，{prompt_file} 为文本文件，{output} 为产物路径。" +
                "图片须写 PNG，视频须写 MP4。平台 CLI 使用自己的登录。", 12, 8));
            var vertical = new StackPanel();
            var expander = new Expander { Header = "视频 API 连接配置", Foreground = Window.Foreground,
                Content = vertical, Margin = new Thickness(0, 15, 0, 0) };
            layout.Children.Add(expander);
            var submit = AgentField(vertical, "提交地址（POST）", agentOptions.SubmitUrl);
            var query = AgentField(vertical, "查询地址（GET，可选，包含 {id}）", agentOptions.StatusUrl);
            var token = AgentField(vertical, "令牌环境变量名（可选）", agentOptions.TokenVariable);
            var body = AgentField(vertical, "提交 JSON 模板（可选，正文使用 {prompt}）", agentOptions.ApiBody, 60);
            var id = AgentField(vertical, "任务号字段，例如 data.id", agentOptions.IdField);
            var file = AgentField(vertical, "视频链接字段，例如 data.video_url", agentOptions.ResultField);
            var state = AgentField(vertical, "任务状态字段（可选）", agentOptions.StateField);
            var completed = AgentField(vertical, "成功状态值（可选）", agentOptions.SuccessState);
            var failed = AgentField(vertical, "失败状态值（可选）", agentOptions.FailureState);
            var cost1 = AgentField(layout, "图片单次预估价（元，空白为未知）", agentOptions.ImageCost);
            var cost2 = AgentField(layout, "视频单次预估价（元，空白为未知）", agentOptions.VideoCost);
            var limit = AgentField(layout, "单次限额（元，选填）", agentOptions.PerTaskLimit);
            var daily = AgentField(layout, "当日限额（元，选填）", agentOptions.DailyLimit);
            var save = ActionButton("保存渠道设置", delegate
            {
                var options = new AgentOptions
                {
                    Model = agentOptions.Model, ReasoningEffort = agentOptions.ReasoningEffort, ImageRoute = RouteKey(imageRoute), VideoRoute = RouteKey(videoRoute),
                    ImageCli = imageCli.Text.Trim(), ImageArgs = imageArgs.Text, VideoCli = videoCli.Text.Trim(),
                    VideoArgs = videoArgs.Text, SubmitUrl = submit.Text.Trim(), StatusUrl = query.Text.Trim(), TokenVariable = token.Text.Trim(),
                    ApiBody = body.Text, IdField = id.Text.Trim(), ResultField = file.Text.Trim(), StateField = state.Text.Trim(),
                    SuccessState = completed.Text.Trim(), FailureState = failed.Text.Trim(), ImageCost = cost1.Text,
                    VideoCost = cost2.Text, PerTaskLimit = limit.Text, DailyLimit = daily.Text
                };
                if (TryAction(delegate { AgentData.SaveOptions(options); }))
                {
                    agentOptions = options;
                    SetStatus("已保存制作渠道。开始制作时将检查渠道配置。");
                }
            }, true);
            save.Margin = new Thickness(0, 12, 0, 0);
            layout.Children.Add(save);
            content.Children.Add(Panel(layout));
            expander.IsExpanded = agentOptions.VideoRoute == "api";
        }

        private ComboBox RoutePicker(StackPanel parent, string label, string[] routes, string selected)
        {
            parent.Children.Add(Muted(label, 13, 10));
            var select = new ComboBox { MinHeight = 31 };
            AutomationProperties.SetName(select, label);
            foreach (string route in routes)
                select.Items.Add(new ComboBoxItem { Content = AgentMedia.RouteName(route), Tag = route });
            select.SelectedIndex = Math.Max(0, Array.IndexOf(routes, selected));
            parent.Children.Add(select);
            return select;
        }

        private static string RouteKey(ComboBox choice) { return (string)((ComboBoxItem)choice.SelectedItem).Tag; }

        private TextBox AgentExecutable(StackPanel parent, string label, string current)
        {
            var box = AgentField(parent, label, current);
            parent.Children.Add(ActionButton("选择" + label, delegate
            {
                var choose = new OpenFileDialog { Filter = "可执行程序|*.exe", CheckFileExists = true };
                if (choose.ShowDialog(Window) == true) box.Text = choose.FileName;
            }, false));
            return box;
        }

        private TextBox AgentField(StackPanel parent, string label, string value, int height = 31)
        {
            parent.Children.Add(Muted(label, 13, 9));
            var box = CodexTextBox(label, value ?? "", height);
            box.AcceptsReturn = height > 34;
            box.MaxHeight = 160;
            parent.Children.Add(box);
            return box;
        }

        private void ShowBridgeHelp()
        {
            string folder = CurrentAgentProject();
            if (folder == null)
            {
                SetStatus("请先打开要授权读取的项目，再获取连接命令。");
                agentLive = "请先打开项目，再获取连接命令。";
                UpdateAgentUi();
                return;
            }
            BeginPage(2, "连接其它助手", Path.GetFileName(folder));
            AddPageBack("返回 Codex 助手", OpenAgent);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Title("当前项目的 MCP 连接", 20));
            panel.Children.Add(Muted("外部助手可以查项目与剧本、查看制作菜单和页面位置。实际制作请回到软件中确认。命令仅绑定当前项目。", 13, 10));
            var command = AgentField(panel, "在 PowerShell 运行的 Codex 连接命令", AgentRunner.BridgeCommand(folder), 70);
            command.IsReadOnly = true;
            panel.Children.Add(ActionButton("复制连接命令", delegate
            {
                CopyText(command.Text, "已复制连接命令。");
            }, true));
            content.Children.Add(Panel(panel));
        }

        private void RenderAgentBatches()
        {
            if (agentBatchRows == null) return;
            agentBatchRows.Children.Clear();
            agentBatchRows.Children.Add(Title("媒体制作", 18));
            agentBatchRows.Children.Add(Muted("使用输入框中的制作要求。", 13, 5));
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            agentStartMedia = ActionButton("制作图片", delegate { StartMedia("图片"); }, true);
            actions.Children.Add(agentStartMedia);
            actions.Children.Add(ActionButton("制作视频", delegate { StartMedia("视频"); }, false));
            agentBatchRows.Children.Add(actions);
            var skills = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            skills.Children.Add(SkillPicker(agentRoot, MediaSkillKey("图片"), "图片制作"));
            skills.Children.Add(SkillPicker(agentRoot, MediaSkillKey("视频"), "视频制作"));
            agentBatchRows.Children.Add(skills);
            if (agentHistory.Batches.Count == 0) return;
            var selector = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetName(selector, "制作批次");
            foreach (AgentBatch batch in agentHistory.Batches.AsEnumerable().Reverse())
                selector.Items.Add(new ComboBoxItem { Tag = batch,
                    Content = (batch.Kind ?? "制作") + " · " + AgentData.Short(batch.Id ?? "未知", 8) + " · " + batch.Jobs.Count + " 项" });
            selector.SelectedItem = selector.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, agentBatch));
            selector.IsEnabled = !sendingCodexTask;
            selector.SelectionChanged += delegate
            {
                if (selector.SelectedItem == null || sendingCodexTask) return;
                agentBatch = (AgentBatch)((ComboBoxItem)selector.SelectedItem).Tag;
                RenderAgentBatches();
            };
            agentBatchRows.Children.Add(selector);
            if (agentBatch == null) return;
            var selected = agentBatch;
            var summary = Muted(selected.Summary ?? "", 13, 9);
            summary.TextWrapping = TextWrapping.Wrap;
            agentBatchRows.Children.Add(summary);
            if (selected.Blocked)
                agentBatchRows.Children.Add(Notice("本批已暂停 · 请先核对状态不明的任务。待运行项将保留。"));
            if (!sendingCodexTask && selected.Jobs.Count == 1 && selected.Jobs[0].State == "待运行")
            {
                var resume = ActionButton("继续待运行任务", delegate { ContinueMedia(selected, null); }, true);
                resume.IsEnabled = !selected.Blocked;
                agentBatchRows.Children.Add(resume);
            }
            foreach (AgentJob job in selected.Jobs)
            {
                var state = Muted(job.State + " · " + AgentData.Short(job.Prompt, 80)
                    + (String.IsNullOrEmpty(job.ExternalId) ? "" : " · 平台任务 " + job.ExternalId)
                    + (String.IsNullOrEmpty(job.Detail) ? "" : "\n" + job.Detail), 13, 8);
                state.TextWrapping = TextWrapping.Wrap;
                agentBatchRows.Children.Add(state);
                var row = new WrapPanel();
                if (job.Path != null)
                {
                    row.Children.Add(ActionButton("打开产物", delegate
                    {
                        TryAction(delegate
                        {
                            string path = AgentData.Inside(AgentData.DirectoryFor(agentRoot), job.Path);
                            if (!File.Exists(path)) throw new InvalidOperationException("产物文件已移动或缺失。");
                            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                        });
                    }, false));
                    row.Children.Add(ActionButton("导出副本", delegate { ExportSystemMedia(agentRoot, job); }, false));
                }
                if (!sendingCodexTask && job.Path == null && job.NeedsReconciliation)
                {
                    if (selected.Route == "api" && !String.IsNullOrEmpty(job.ExternalId) || HasRecoverableMedia(job))
                        row.Children.Add(ActionButton("核对原平台任务", delegate { ContinueMedia(selected, job); }, false));
                    row.Children.Add(ActionButton("登记人工核对", delegate { RecordMediaCheck(selected, job); }, false));
                }
                row.Children.Add(ActionButton("问 Codex", delegate
                {
                    AskAgentAbout("项目制作：" + selected.Kind + "，内部任务：" + job.Id + "，平台任务：" + (job.ExternalId ?? "未知")
                        + "，状态：" + job.State + "。\n制作要求：" + job.Prompt + "\n产物：" + (job.Path ?? "")
                        + "\n提示：" + (job.Detail ?? ""));
                }, false));
                agentBatchRows.Children.Add(row);
            }
        }

        private static string MediaSkillKey(string kind) { return kind == "图片" ? "media.image" : "media.video"; }

        private async void StartMedia(string kind)
        {
            if (!ReadyAgent()) return;
            string prompt = agentDraft.Trim();
            if (String.IsNullOrWhiteSpace(prompt))
            {
                agentLive = "请先填写制作要求。";
                UpdateAgentUi();
                if (agentInput != null) agentInput.Focus();
                return;
            }
            if (agentRoot == null)
            {
                agentLive = "请先打开项目，用于保存产物和任务记录。";
                UpdateAgentUi();
                return;
            }
            string route = AgentMedia.Route(agentOptions, kind);
            SelectedSkill skill;
            if (!ResolveSkill(agentRoot, MediaSkillKey(kind), out skill)) return;
            var next = new AgentBatch { Id = AgentData.Id(), Kind = kind, Route = route, Profile = MediaProfile(agentOptions, kind, route),
                Skill = skill == null ? null : skill.Reference,
                Jobs = new List<AgentJob> { new AgentJob { Id = AgentData.Id(), Prompt = prompt, State = "待运行" } } };
            if (skill != null && !await PrepareMediaPrompts(next, skill)) return;
            ContinueMedia(next, null);
        }

        private async Task<bool> PrepareMediaPrompts(AgentBatch batch, SelectedSkill skill)
        {
            var gate = BeginAgentRun();
            if (gate == null) return false;
            string work = Path.Combine(AgentData.Local, "任务临时", AgentData.Id());
            string model = agentOptions.Model;
            string exe = codexStatus.Path;
            var cancel = agentCancel.Token;
            try
            {
                Directory.CreateDirectory(work);
                string schema = Path.Combine(work, "form.json");
                string result = Path.Combine(work, "prepared.json");
                File.WriteAllText(schema, "{\"type\":\"object\",\"properties\":{\"prompts\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"prompts\"],\"additionalProperties\":false}");
                agentLive = "正在用「" + skill.Reference.Name + "」准备提示词…";
                UpdateAgentUi();
                string prompt = "按所选 Skill 整理以下" + batch.Kind + "制作提示词。保留用户的目标、硬性约束、语言和顺序。"
                    + "输入有几项就输出几项。仅准备下方提示词，不发起任何媒体制作。\n"
                    + AgentData.Json().Serialize(batch.Jobs.Select(job => job.Prompt).ToArray());
                await Task.Run(() => AgentRunner.Run(exe, work, prompt, model, null,
                    schema, result, null, null, null, false, null, cancel, skill: skill));
                cancel.ThrowIfCancellationRequested();
                var data = AgentData.Read<Dictionary<string, object>>(result);
                var prepared = AgentData.Value(data, "prompts") as System.Collections.IList;
                if (prepared == null || prepared.Count != batch.Jobs.Count ||
                    prepared.Cast<object>().Any(value => String.IsNullOrWhiteSpace(value as string)))
                    throw new InvalidDataException("整理后的提示词数量不符或有空项。");
                for (int index = 0; index < batch.Jobs.Count; index++)
                {
                    batch.Jobs[index].OriginalPrompt = batch.Jobs[index].Prompt;
                    batch.Jobs[index].Prompt = (string)prepared[index];
                }
                agentLive = "提示词已准备，请核对后确认制作。";
                return !exitAfterCodexTask;
            }
            catch (Exception error)
            {
                agentLive = cancel.IsCancellationRequested ? "提示词准备已停止。" :
                    "提示词准备未完成 · " + AgentData.Short(error.Message, 350);
                return false;
            }
            finally
            {
                CleanAgentWork(work);
                EndAgentRun(gate);
            }
        }

        private static string MediaProfile(AgentOptions options, string kind, string route)
        {
            object settings;
            if (route == "cli" && kind == "图片") settings = new { options.ImageCli, options.ImageArgs };
            else if (route == "cli") settings = new { options.VideoCli, options.VideoArgs };
            else if (route == "api")
                settings = new { options.SubmitUrl, options.StatusUrl, options.TokenVariable, options.ApiBody,
                    options.IdField, options.ResultField, options.StateField, options.SuccessState, options.FailureState };
            else settings = new { route };
            return AgentData.Hash(AgentData.Json().Serialize(settings));
        }

        private bool HasRecoverableMedia(AgentJob job)
        {
            try
            {
                string folder = AgentMedia.JobFolder(agentRoot, job);
                var receipt = AgentMedia.ReadJobReceipt(folder, job.LastCheck) ?? AgentMedia.ReadJobReceipt(folder, null);
                return receipt != null && (receipt.State == "已取回" || receipt.State == "平台已失败");
            }
            catch (Exception) { return false; }
        }

        private void RecordMediaCheck(AgentBatch batch, AgentJob job)
        {
            if (sendingCodexTask || !SameProject(agentRoot, CurrentAgentProject()) || !job.NeedsReconciliation) return;
            string evidence;
            if (!Prompt("登记人工核对", "在原平台确认此任务未受理、已失败、已取消，或确认已取回产物后，填写实际状态和核对依据。仍在运行或查不到时请继续暂停。",
                "保存核对结果", "保持暂停", true, out evidence, "实际状态与核对依据")) return;
            if (String.IsNullOrWhiteSpace(evidence))
            {
                agentLive = "请填写实际状态和核对依据。";
                UpdateAgentUi();
                return;
            }
            var gate = BeginAgentRun();
            if (gate == null) return;
            try
            {
                job.State = "已人工核对";
                job.ReconciledUtc = AnalysisData.Now();
                job.Detail = AgentData.Short(evidence.Trim(), 900);
                batch.Summary = batch.Blocked ? "本批仍有待核对任务。"
                    : "原任务状态已登记，可手动继续待运行项。";
                agentLive = batch.Summary;
                SaveAgent();
            }
            finally { EndAgentRun(gate); }
        }

        private async void ContinueMedia(AgentBatch batch, AgentJob original)
        {
            if (original == null && batch.Blocked)
            {
                agentLive = "本批有状态不明的任务。核对完成后再继续待运行项。";
                UpdateAgentUi();
                return;
            }
            if (!ReadyAgent() || agentRoot == null) return;
            if (batch.Profile != MediaProfile(agentOptions, batch.Kind, batch.Route))
            {
                agentLive = "制作渠道设置已变化。请恢复该批次原配置后继续或核对原任务。";
                UpdateAgentUi();
                return;
            }
            List<AgentJob> selected = original == null ? batch.Jobs.Where(j => j.State == "待运行").ToList() :
                new List<AgentJob> { original };
            if (selected.Count == 0 || original != null && !original.NeedsReconciliation) return;
            bool initial = !agentHistory.Batches.Contains(batch);
            try
            {
                if (original == null || !String.IsNullOrEmpty(original.ExternalId))
                    AgentMedia.Validate(agentOptions, batch.Kind, batch.Route);
                if (agentImages.Count != 0 && batch.Route != "builtin" && initial)
                    throw new InvalidOperationException("这版外部渠道配置处理文字任务。请清空参考图，或者为图片改选内置渠道。");
                if (original == null) AgentMedia.Budget(agentOptions, batch.Kind, selected.Count);
            }
            catch (Exception e)
            {
                agentLive = AgentData.Short(e.Message, 350);
                UpdateAgentUi();
                return;
            }
            string authorize;
            decimal? cost = AgentMedia.Cost(agentOptions, batch.Kind);
            string ask = original != null ? "核对已经执行过的任务与收据" +
                (String.IsNullOrEmpty(original.ExternalId) ? "。" : "。平台任务号 " + original.ExternalId + "。") :
                "Lacuna 将通过「" + AgentMedia.RouteName(batch.Route) + "」发起 " + selected.Count + " 项" + batch.Kind +
                "制作。\n预估总额：" + (cost.HasValue ? (cost.Value * selected.Count).ToString("0.##", CultureInfo.InvariantCulture) + " 元" : "未知，以平台实际计费为准") + "。";
            if (original == null && batch.Skill != null)
                ask += "\n\nSkill · " + batch.Skill.Name + "\n本次将提交以下提示词：\n\n" +
                    String.Join("\n\n", selected.Select((job, index) => (index + 1) + " ｜ " + job.Prompt));
            if (!Prompt(original != null ? "核对原任务" : "确认制作", ask,
                original != null ? "开始核对" : "确认开始", "取消", false, out authorize)) return;
            var gate = BeginAgentRun();
            if (gate == null) return;
            string root = agentRoot;
            string exe = codexStatus.Path;
            var options = AgentData.Json().Deserialize<AgentOptions>(AgentData.Json().Serialize(agentOptions));
            var cancel = agentCancel.Token;
            try
            {
                string dataRoot = AgentData.DirectoryFor(root);
                if (initial)
                {
                    batch.ReferencePaths = new List<string>();
                    foreach (string file in agentImages)
                    {
                        AgentMedia.ValidateOutput(file, "图片");
                        string relative = Path.Combine("参考图", batch.Id,
                            (batch.ReferencePaths.Count + 1) + Path.GetExtension(file).ToLowerInvariant());
                        string destination = AgentData.Inside(dataRoot, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(file, destination);
                        batch.ReferencePaths.Add(relative);
                    }
                    agentHistory.Batches.Add(batch);
                }
                var images = (batch.ReferencePaths ?? new List<string>())
                    .Select(p => AgentData.Inside(dataRoot, p)).ToList();
                if (original == null && images.Any(p => !File.Exists(p)))
                    throw new InvalidOperationException("已确认的参考图缺失。请找回参考图后继续。");
                agentBatch = batch;
                if (!SaveAgent()) return;
                foreach (AgentJob item in selected)
                {
                    if (cancel.IsCancellationRequested || original == null && batch.Blocked) break;
                    string jobFolder = AgentMedia.JobFolder(root, item);
                    string previous = item.LastCheck;
                    if (original != null) item.LastCheck = AgentData.Id();
                    string work = AgentMedia.AttemptFolder(jobFolder, original == null ? null : item.LastCheck);
                    item.State = "运行中";
                    item.StartedUtc = DateTime.UtcNow.ToString("o");
                    item.Detail = "";
                    agentLive = "Codex 正在通过 " + AgentMedia.RouteName(batch.Route) + " 执行" + batch.Kind + "任务。";
                    if (!SaveAgent()) break;
                    RenderAgentBatches();
                    UpdateAgentUi();
                    try
                    {
                        string relative = await Task.Run(() => RunMedia(exe, root, jobFolder, work, batch, item, previous, original != null, options, images, cancel));
                        item.Path = relative;
                        item.State = "已完成 · 待查看";
                    }
                    catch (Exception e)
                    {
                        item.Detail = AgentData.Short(e.Message, 430);
                        item.State = original != null || File.Exists(Path.Combine(work, "invoked")) ? "需核对 · 已尝试提交" :
                            (e is OperationCanceledException ? "已停止" : "执行未完成");
                    }
                    var receipt = AgentMedia.ReadJobReceipt(jobFolder, item.LastCheck);
                    if (receipt != null && !String.IsNullOrWhiteSpace(receipt.ExternalId)) item.ExternalId = receipt.ExternalId;
                    if (receipt != null && receipt.State == "平台已失败")
                    {
                        item.State = "已核对 · 平台已失败";
                        item.Detail = "平台已返回终止失败状态。";
                        item.ReconciledUtc = AnalysisData.Now();
                    }
                    if (!SaveAgent()) break;
                    if (batch.Blocked || exitAfterCodexTask || cancel.IsCancellationRequested) break;
                }
                int completed = batch.Jobs.Count(j => j.Path != null);
                int pending = batch.Jobs.Count(j => j.State == "待运行");
                batch.Summary = "批次汇总 · 完成 " + completed + " / " + batch.Jobs.Count +
                    " · 待运行 " + pending + " · 其余 " + (batch.Jobs.Count - completed - pending) + " 项可查看状态。";
                if (batch.Blocked) batch.Summary += "\n本批已暂停，等待核对原任务。";
                agentLive = batch.Summary;
                SaveAgent();
            }
            catch (Exception e)
            {
                agentLive = "批次保存或恢复未完成 · " + AgentData.Short(e.Message, 350);
                SaveAgent();
            }
            finally { EndAgentRun(gate); }
        }

        private static string RunMedia(string exe, string project, string jobFolder, string work, AgentBatch batch,
            AgentJob item, string previousCheck, bool reconcile, AgentOptions options, IList<string> images, CancellationToken cancel, bool assetImage = false, string targetSize = null, SelectedSkill imageSkill = null, int timeoutMs = 0)
        {
            string existingId = item.ExternalId;
            if (reconcile)
            {
                var previous = AgentMedia.ReadJobReceipt(jobFolder, previousCheck);
                if (previous == null || previous.State == "已发起")
                {
                    var initial = AgentMedia.ReadJobReceipt(jobFolder, null);
                    if (initial != null && (previous == null || initial.State != "已发起"))
                    {
                        previous = initial;
                        previousCheck = null;
                    }
                }
                if (previous != null && previous.State == "已取回")
                    return CollectMedia(project, AgentData.Inside(AgentMedia.AttemptFolder(jobFolder, previousCheck), previous.Output), batch.Kind, item.Id);
                if (previous != null && previous.State == "平台已失败")
                {
                    AgentData.Save(Path.Combine(work, AgentMedia.ReceiptFile), previous);
                    throw new InvalidOperationException("平台已返回终止失败状态。");
                }
                if (batch.Route != "api") throw new InvalidOperationException("外部 CLI 或内置图片请按原渠道核对后登记。");
                if (previous != null && !String.IsNullOrWhiteSpace(previous.ExternalId)) existingId = previous.ExternalId;
                if (String.IsNullOrWhiteSpace(existingId))
                    throw new InvalidOperationException("缺少平台任务号。请在外部平台核对。");
            }
            Directory.CreateDirectory(work);
            bool responsesApi = FlowChannels.IsResponsesApi(batch.Route);
            bool builtin = batch.Route == "builtin" || responsesApi;
            if (responsesApi) CodexAuthMode.EnsureResponsesApi();
            else if (builtin) CodexAuthMode.EnsureBuiltinImage();
            cancel.ThrowIfCancellationRequested();
            bool direct = !builtin && (assetImage && batch.Route == "cli" || CodexAuthMode.IsApi);
            using (var authorization = direct ? new FileStream(Path.Combine(work, "authorization.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose) : null)
            {
                if (!reconcile && (!assetImage || builtin)) AgentMedia.Budget(options, batch.Kind, 1, item.Id);
                if (builtin) using (File.Open(Path.Combine(work, "invoked"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                if (!builtin)
                    AgentData.Save(Path.Combine(work, AgentMedia.RequestFile), new MediaRequest
                    {
                        Kind = batch.Kind, Route = batch.Route, Prompt = item.Prompt,
                        Options = options, ExternalId = reconcile ? existingId : null, AssetReservationId = assetImage ? item.Id : null,
                        ReferencePath = assetImage && !reconcile && images != null ? images.FirstOrDefault() : null, TargetSize = targetSize, TimeoutMs = timeoutMs
                    });
                string instruction = builtin ? item.Prompt :
                    "你是制作执行者。立即调用 MCP 工具 mcp__oneclick_film__execute_approved_media 一次。工具读取用户已批准的请求。"
                    + "以工具返回的状态为准，不要调用其他工具或替代供应商。";
                AgentMedia.SetThreadExecutionState(0x80000001u);
                try
                {
                    string session = null;
                    var candidate = new AgentMedia.StableImage();
                    var trace = assetImage && builtin ? new ImageRunTrace(work) : null;
                    if (trace != null) trace.Mark(images != null && images.Count > 0 ? "正在通过 HTTPS 提交已确认的参考图请求" : "正在通过 HTTPS 提交已确认的图片请求");
                    int generating = 0;
                    var imageClock = System.Diagnostics.Stopwatch.StartNew();
                    Action<AgentEvent> observe = null;
                    if (builtin && assetImage)
                        observe = e =>
                        {
                            if (e.Type == "image_started" || e.Type == "image_finished") Interlocked.Exchange(ref generating, 1);
                            trace.Event(e);
                            if (e.Type != "session" || String.IsNullOrWhiteSpace(e.Text)) return;
                            File.WriteAllText(Path.Combine(work, AgentMedia.BuiltinSessionFile), e.Text);
                            Interlocked.Exchange(ref session, e.Text);
                        };
                    Func<bool> ready = null;
                    if (assetImage) ready = () =>
                    {
                        if (builtin && timeoutMs > 0 && Volatile.Read(ref generating) == 0 && imageClock.ElapsedMilliseconds >= timeoutMs)
                            throw new TimeoutException("等待图像工具开始生成超时。");
                        try
                        {
                            string path;
                            if (builtin)
                            {
                                string id = Volatile.Read(ref session);
                                if (id == null) return false;
                                path = AgentMedia.FindBuiltinOutput(id, true);
                            }
                            else
                            {
                                var receipt = AgentMedia.ReadJobReceipt(work, null);
                                if (receipt == null || receipt.State != "已取回") return false;
                                path = AgentData.Inside(work, receipt.Output);
                            }
                            return candidate.Check(path);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (InvalidDataException) { }
                        catch (FormatException) { }
                        catch (NotSupportedException) { }
                        catch (InvalidOperationException) { }
                        catch (System.Runtime.InteropServices.COMException) { }
                        return false;
                    };
                    string source;
                    if (direct)
                    {
                        var receipt = AgentMedia.Execute(work, cancel);
                        source = AgentData.Inside(work, receipt.Output);
                    }
                    else if (responsesApi || builtin && CodexAuthMode.IsApi)
                    {
                        try
                        {
                            source = OpenAiResponsesClient.GenerateImage(work, instruction, options, images, targetSize, imageSkill, timeoutMs, cancel, exe);
                            if (trace != null) trace.Mark("已通过 Responses API 取回图片");
                        }
                        catch (Exception ex)
                        {
                            if (trace != null) trace.Mark("Responses API 图像请求失败 · " + AgentData.Short(CodexAuthMode.RedactDiagnostic(ex.Message), 200));
                            throw;
                        }
                    }
                    else if (builtin && assetImage && imageSkill == null && images != null &&
                        images.Count > 0 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        source = new NativeImageRetry(work, instruction, options, images, trace, cancel)
                            .Run(exe, timeoutMs > 0 ? timeoutMs : 360000);
                    }
                    else
                    {
                        var result = AgentRunner.Run(exe, work, builtin ? "用内置工具生成一张图。\n" + instruction : instruction, options.Model, null, null, null,
                            builtin && !reconcile ? images : null, project, builtin ? null : work, builtin, observe, cancel, skill: builtin ? imageSkill : null, timeoutMs: assetImage && timeoutMs > 0 ? timeoutMs + (builtin ? ImageOutputSettings.ReturnGraceMs : 180000) : timeoutMs, resultReady: ready,
                            onDiagnostic: trace == null ? null : (Action<string>)trace.Diagnostic, assetImage: assetImage);
                        var media = builtin ? null : AgentData.Read<MediaReceipt>(Path.Combine(work, AgentMedia.ReceiptFile));
                        if (!builtin && (media == null || media.State != "已取回"))
                            throw new InvalidOperationException("本轮 Codex 没有通过工具取回产物。 " +
                                AgentData.Short(result.TransportNote ?? result.Answer, 250));
                        source = builtin ? AgentMedia.BuiltinOutput(result.Session, assetImage) : AgentData.Inside(work, media.Output);
                    }
                    if (builtin && assetImage && !responsesApi && !CodexAuthMode.IsApi)
                    {
                        string name = "output" + Path.GetExtension(source).ToLowerInvariant();
                        string archived = AgentData.Inside(work, name);
                        File.Copy(source, archived, false);
                        AgentMedia.ValidateOutput(archived, "图片");
                        AgentData.Save(Path.Combine(work, AgentMedia.ReceiptFile),
                            new MediaReceipt { State = "已取回", Output = name });
                        if (trace != null) trace.Mark("本次图片已取回并写入成功回执");
                        source = archived;
                    }
                    if (assetImage)
                    {
                        string relative = Path.GetFullPath(source).Substring(Path.GetFullPath(work).TrimEnd('\\','/').Length + 1);
                        if (!String.Equals(AgentData.Inside(work, relative), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("本次回图路径与任务收据不一致。");
                        return Path.Combine("运行收据", item.Id, relative);
                    }
                    return CollectMedia(project, source, batch.Kind, item.Id);
                }
                finally
                {
                    try
                    {
                        string request = Path.Combine(work, AgentMedia.RequestFile);
                        if (File.Exists(request)) File.Delete(request);
                    }
                    finally { AgentMedia.SetThreadExecutionState(0x80000000u); }
                }
            }
        }

        private static string CollectMedia(string project, string source, string kind, string id)
        {
            AgentMedia.ValidateOutput(source, kind);
            string relative = Path.Combine("产物", id + Path.GetExtension(source).ToLowerInvariant());
            string destination = AgentData.Inside(AgentData.DirectoryFor(project), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + AgentData.Id() + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return relative;
        }

    }
}
