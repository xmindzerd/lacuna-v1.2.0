using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private AgentOptions agentOptions;
        private AgentHistory agentHistory;
        private AgentChat agentChat;
        private AgentBatch agentBatch;
        private string agentRoot;
        private string agentDraft = "";
        private string agentLive = "";
        private Stopwatch agentTurnElapsed;
        private string agentDestination;
        private string agentProjectPage, agentPageProject;
        private string agentHistoryHash;
        private List<string> agentImages = new List<string>();
        private TextBlock agentStatusLine, agentUsageLine, agentImagesLine;
        private TextBox agentInput;
        private RichTextBox agentConsole;
        private AgentChat visibleAgentChat;
        private int visibleAgentMessages;
        private Button agentNavigate, agentSend, agentStructured, agentStop, agentStartMedia, agentMenuButton, agentClearImages;
        private StackPanel agentPanel, agentBatchRows;
        private ContentControl agentDock;
        private Window agentWindow;
        private CancellationTokenSource agentCancel, installCancel;
        private TextBlock installStatus;
        private bool loginRefresh, agentSaveFailed;
        private string installState = "";

        private void InitializeAgent()
        {
            if (!TryAction(delegate { agentOptions = AgentData.Options(); }))
                agentOptions = new AgentOptions { ImageRoute = "builtin", VideoRoute = "cli" };
        }

        private void OnShellActivated()
        {
            if(shell.apiTesting)return;
            if (!systemBusy) TryAction(() => preferences = store.LoadPreferences());
            if (loginRefresh || codexDetails != null)
            {
                loginRefresh = false;
                RefreshCodex();
            }
        }

        private string CurrentAgentProject()
        {
            return project == null ? null : projectFolder;
        }

        private bool LoadAgentContext()
        {
            string folder = CurrentAgentProject();
            if (agentHistory != null && (sendingCodexTask || SameProject(agentRoot, folder))) return true;
            return TryAction(delegate
            {
                var history = AgentData.LoadHistory(folder);
                agentRoot = folder;
                agentHistoryHash = ReadAgentHistoryHash(folder);
                agentHistory = history;
                agentChat = history.Chats.LastOrDefault();
                agentBatch = history.Batches.LastOrDefault();
                agentImages.Clear();
                agentDraft = "";
                agentLive = "";
                agentDestination = null;
            });
        }

        private static bool SameProject(string a, string b)
        {
            if(String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b))return String.IsNullOrEmpty(a) && String.IsNullOrEmpty(b);
            return String.Equals(a.Replace('/','\\').TrimEnd('\\'), b.Replace('/','\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private void OpenAgent()
        {
            if (!LoadAgentContext()) return;
            if (agentWindow != null)
            {
                RebuildAgentPanel();
                agentWindow.Show();
                agentWindow.Activate();
                return;
            }
            BeginPage(3, "Codex 助手", agentRoot == null ? "本机对话" : Path.GetFileName(agentRoot));
            agentDock = new ContentControl();
            content.Children.Add(agentDock);
            BuildAgentPanel();
            agentDock.Content = agentPanel;
        }

        private void BuildAgentPanel()
        {
            agentPanel = new StackPanel();
            if (agentWindow != null) agentPanel.LayoutTransform = SystemScale();
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
            if (agentWindow != null) header.Children.Add(Title("Codex 助手", 22));
            if (agentWindow != null)
                header.Children.Add(Muted(agentRoot == null ? "本机对话" : Path.GetFileName(agentRoot), 13, 4));
            var toolbar = new WrapPanel { Margin = new Thickness(0, agentWindow == null ? 0 : 11, 0, 0) };
            agentMenuButton = ActionButton("制作菜单", AgentMenu, false);
            toolbar.Children.Add(agentMenuButton);
            var fresh = ActionButton("新对话", delegate { NewAgentChat(); }, false);
            fresh.IsEnabled = !sendingCodexTask;
            toolbar.Children.Add(fresh);
            toolbar.Children.Add(ActionButton("制作渠道", ShowAgentRoutes, false));
            toolbar.Children.Add(ActionButton("连接其它助手", ShowBridgeHelp, false));
            toolbar.Children.Add(ActionButton(agentWindow != null ? "停靠回工作台" : "弹出对话窗口", ToggleAgentWindow, false));
            if (agentWindow != null)
                toolbar.Children.Add(ActionButton(agentWindow.Topmost ? "取消置顶" : "窗口置顶", delegate
                {
                    agentWindow.Topmost = !agentWindow.Topmost;
                    RebuildAgentPanel();
                }, false));
            header.Children.Add(toolbar);
            agentUsageLine = Muted("", 12, 8);
            agentUsageLine.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(agentUsageLine);
            agentPanel.Children.Add(header);

            var conversation = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };
            var list = new ComboBox { Margin = new Thickness(0, 0, 0, 9), DisplayMemberPath = "Title" };
            AutomationProperties.SetName(list, "历史对话");
            list.ItemsSource = agentHistory.Chats.ToList().AsEnumerable().Reverse();
            list.SelectedItem = agentChat;
            list.IsEnabled = !sendingCodexTask;
            list.SelectionChanged += delegate
            {
                if (list.SelectedItem == null || sendingCodexTask) return;
                agentChat = (AgentChat)list.SelectedItem;
                agentLive = "";
                UpdateAgentUi();
            };
            if (agentHistory.Chats.Count > 0) conversation.Children.Add(list);
            agentStatusLine = Muted("", 13, 0);
            agentStatusLine.Margin = new Thickness(0, 0, 0, 9);
            agentStatusLine.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetAutomationId(agentStatusLine, "AgentStatus");
            conversation.Children.Add(agentStatusLine);
            agentNavigate = ActionButton("前往助手指出的位置", GoToAgentDestination, false);
            agentNavigate.HorizontalAlignment = HorizontalAlignment.Left;
            conversation.Children.Add(agentNavigate);
            agentConsole = new RichTextBox
            {
                MinHeight = 148, MaxHeight = 310, IsReadOnly = true,
                Background = Brush("Panel"), BorderBrush = Brushes.Transparent,
                Padding = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 14 }
            };
            AutomationProperties.SetName(agentConsole, "聊天记录");
            visibleAgentChat = null;
            visibleAgentMessages = 0;
            conversation.Children.Add(agentConsole);
            agentPanel.Children.Add(Panel(conversation));

            var composer = new StackPanel { Margin = new Thickness(13) };
            composer.Children.Add(Title("输入问题或制作要求", 14));
            agentInput = CodexTextBox("对话或制作要求", agentDraft, 72);
            agentInput.AcceptsReturn = true;
            agentInput.MaxLength = 12000;
            agentInput.MaxHeight = 170;
            agentInput.TextChanged += delegate
            {
                agentDraft = agentInput.Text;
                UpdateComposerActions();
            };
            composer.Children.Add(agentInput);
            agentImagesLine = Muted("", 12, 5);
            agentImagesLine.TextWrapping = TextWrapping.Wrap;
            composer.Children.Add(agentImagesLine);
            var composerActions = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            toolbar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            agentSend = ActionButton("发送给 Codex", delegate { StartAgentTurn(false); }, true);
            agentSend.Margin = new Thickness(8, 8, 0, 0);
            agentSend.VerticalAlignment = VerticalAlignment.Bottom;
            agentStop = ActionButton("停止当前操作", StopAgent, false);
            AddRight(composerActions, agentSend);
            toolbar.Children.Add(ActionButton("添加参考图", AddAgentImages, false));
            toolbar.Children.Add(SkillPicker(agentRoot, "assistant.chat", "发送给 Codex"));
            agentStructured = ActionButton("结构化任务", delegate { StartAgentTurn(true); }, false);
            toolbar.Children.Add(agentStructured);
            toolbar.Children.Add(SkillPicker(agentRoot, "assistant.structured", "结构化任务"));
            agentClearImages = ActionButton("清空参考图", delegate { agentImages.Clear(); UpdateAgentUi(); }, false);
            toolbar.Children.Add(agentClearImages);
            toolbar.Children.Add(agentStop);
            foreach (Button action in toolbar.Children)
                action.Margin = new Thickness(0, 0, 7, 5);
            composerActions.Children.Add(toolbar);
            composer.Children.Add(composerActions);
            var body = Panel(composer);
            body.Margin = new Thickness(0, 10, 0, 0);
            agentPanel.Children.Add(body);

            agentBatchRows = new StackPanel { Margin = new Thickness(15) };
            var batches = Panel(agentBatchRows);
            batches.Margin = new Thickness(0, 10, 0, 0);
            agentPanel.Children.Add(batches);
            RenderAgentBatches();
            UpdateAgentUi();
        }

        private void RebuildAgentPanel()
        {
            BuildAgentPanel();
            if (agentWindow != null)
                ((ScrollViewer)agentWindow.Content).Content = agentPanel;
            else if (agentDock != null) agentDock.Content = agentPanel;
        }

        private void ToggleAgentWindow()
        {
            if (agentWindow != null)
            {
                var old = agentWindow;
                agentWindow = null;
                old.Close();
                OpenAgent();
                return;
            }
            if (agentDock != null) agentDock.Content = null;
            var window = new Window
            {
                Title = "Codex 助手 · " + Program.Version,
                MinWidth = 560, MinHeight = 490, Width = 790, Height = 780,
                Background = Window.Background, Foreground = Window.Foreground, FontFamily = Window.FontFamily,
                Icon = Window.Icon, UseLayoutRounding = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new ScrollViewer
                {
                    Background = Window.Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(15)
                },
                Topmost = false
            };
            agentWindow = window;
            window.SourceInitialized += delegate { SetTitleBarTheme(window); };
            window.Closed += delegate
            {
                if (agentWindow != window) return;
                agentWindow = null;
                if (!exiting) OpenAgent();
            };
            RebuildAgentPanel();
            window.Show();
            if (agentDock != null)
            {
                var dockActions = new StackPanel();
                dockActions.Children.Add(Muted("对话已在独立窗口打开。", 13, 8));
                dockActions.Children.Add(ActionButton("返回停靠", ToggleAgentWindow, true));
                agentDock.Content = dockActions;
            }
        }

        private void SetAgentDraft(string text)
        {
            agentDraft = text;
            if (agentInput != null) agentInput.Text = text;
        }

        private void NewAgentChat()
        {
            if (sendingCodexTask) return;
            agentChat = null;
            agentLive = "已打开新对话。发送第一条消息后保存。";
            agentDestination = null;
            RebuildAgentPanel();
        }

        private void AgentMenu()
        {
            if (sendingCodexTask) return;
            var menu = new ContextMenu { PlacementTarget = agentMenuButton, Placement = PlacementMode.Bottom };
            AddMenuItem(menu, "查看当前项目和剧本", delegate { SetAgentDraft("请读取当前项目和已导入的剧本，概述内容并指出有哪些信息待核对。"); });
            AddMenuItem(menu, "分析剧本 · 结构化任务", delegate { SetAgentDraft("分析当前项目剧本，概述剧情、人物、场景与道具，并给出可回查的原文依据。"); StartAgentTurn(true); });
            AddMenuItem(menu, "制作图片", delegate { StartMedia("图片"); });
            AddMenuItem(menu, "制作视频", delegate { StartMedia("视频"); });
            AddMenuItem(menu, "设置 Codex", ShowSettings);
            agentMenuButton.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private static void AddMenuItem(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        private void AddAgentImages()
        {
            if (sendingCodexTask) return;
            var dialog = new OpenFileDialog { Filter = "参考图片|*.png;*.jpg;*.jpeg;*.webp", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(agentWindow ?? Window) != true) return;
            agentImages = dialog.FileNames.Where(f => new FileInfo(f).Length <= 20 * 1024 * 1024).Take(4).ToList();
            UpdateAgentUi();
        }

        private bool ReadyAgent()
        {
            if (sendingCodexTask || checkingCodex || installCancel != null) return false;
            if (!SameProject(agentRoot, CurrentAgentProject()))
            {
                agentLive = "当前项目已经变更。请重新打开 Codex 助手确认项目。";
                UpdateAgentUi();
                return false;
            }
            if (codexStatus != null && codexStatus.State == CodexState.SignedIn && (CodexAuthMode.IsApi || codexStatus.Path != null)) return true;
            agentLive = CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先在设置确认本机 Codex 已登录。";
            UpdateAgentUi();
            RefreshCodex();
            return false;
        }

        private IDisposable BeginAgentRun()
        {
            string root = AgentData.DirectoryFor(agentRoot);
            try
            {
                Directory.CreateDirectory(root);
                var gate = new ProjectTaskLease(agentRoot, "Codex助手");
                try
                {
                    string current = ReadAgentHistoryHash(agentRoot);
                    if (current != agentHistoryHash)
                    {
                        agentHistory = AgentData.LoadHistory(agentRoot);
                        agentHistoryHash = current;
                        agentChat = agentHistory.Chats.LastOrDefault();
                        agentBatch = agentHistory.Batches.LastOrDefault();
                        gate.Dispose();
                        agentLive = "已读回另一窗口保存的最新记录。请核对后重新操作。";
                        RebuildAgentPanel();
                        return null;
                    }
                    sendingCodexTask = true;
                    agentSaveFailed = false;
                    agentCancel = new CancellationTokenSource();
                    UpdateCodexDisplay();
                    return gate;
                }
                catch
                {
                    try { gate.Dispose(); } finally { ClearAgentRun(); }
                    throw;
                }
            }
            catch (IOException)
            {
                agentLive = "该项目已有 Lacuna 任务在运行，或项目文件夹不可写。";
                UpdateAgentUi();
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                agentLive = "项目文件夹不可写。";
                UpdateAgentUi();
                return null;
            }
        }

        private void ClearAgentRun()
        {
            if(agentCancel!=null)agentCancel.Dispose();
            agentCancel=null;
            sendingCodexTask=false;
        }

        private void EndAgentRun(IDisposable gate)
        {
            try { gate.Dispose(); } finally { ClearAgentRun(); }
            if (agentPanel != null) RebuildAgentPanel();
            UpdateCodexDisplay();
            if (exitAfterCodexTask)
            {
                exitAfterCodexTask = false;
                if (!agentSaveFailed) FinishExit();
                else SetStatus("保存任务记录失败。请核对项目目录后重试保存。");
            }
        }

        private void StopAgent()
        {
            if (agentCancel == null) return;
            agentLive = "正在停止本机任务。已提交的外部任务可以在原平台继续运行。";
            agentCancel.Cancel();
            UpdateAgentUi();
        }

        private bool SaveAgent()
        {
            bool ok = TryAction(delegate
            {
                AgentData.SaveHistory(agentRoot, agentHistory);
                agentHistoryHash = ReadAgentHistoryHash(agentRoot);
            });
            if (!ok)
            {
                agentSaveFailed = true;
                agentLive = "无法保存项目的 Codex 记录。请核对项目目录。";
            }
            else agentSaveFailed = false;
            return ok;
        }

        private static string ReadAgentHistoryHash(string projectPath)
        {
            string file = AgentData.HistoryFile(projectPath);
            return File.Exists(file) ? AgentData.Hash(File.ReadAllText(file)) : "";
        }

        private void ProgressAgent(AgentEvent e)
        {
            if (agentCancel != null && agentCancel.IsCancellationRequested) return;
            if (e.Type == "retry") agentLive = "Codex 连接中断，正在重连" +
                (String.IsNullOrEmpty(e.Text) ? "…" : " · " + e.Text);
            else if (e.Type == "delta") agentLive = "正在回复 · "+e.Text;
            else if (e.Type == "step") agentLive = "Codex 正在运行工具 · " + e.Text;
            else if (e.Type == "tool_error") agentLive = "工具返回错误 · " + e.Text;
            else if (e.Type == "navigate") agentDestination = e.Text;
            else if (agentTurnElapsed != null)
            {
                if (e.Type == "session") agentLive = "正在准备 Codex 对话…";
                else if (e.Type == "started") agentLive = "已交给 Codex，等待回复…";
                else if (e.Type == "message") agentLive = "已收到 Codex 消息，正在等待本轮结束…";
                else if (e.Type == "complete") agentLive = "Codex 已完成，正在保存结果…";
                else if (e.Type == "failed") agentLive = "Codex 报告任务失败，正在保存记录…";
            }
            UpdateAgentUi();
        }

        private void UpdateAgentStatus()
        {
            if (agentStatusLine == null) return;
            string status = String.IsNullOrEmpty(agentLive) ?
                (agentChat == null ? "" : agentChat.State) : agentLive;
            if (agentTurnElapsed != null)
                status += " · 已等 " + (int)agentTurnElapsed.Elapsed.TotalSeconds + " 秒";
            agentStatusLine.Text = status;
            agentStatusLine.Visibility = String.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateAgentUi()
        {
            if (agentConsole == null) return;
            UpdateAgentStatus();
            bool unlock = !sendingCodexTask;
            UpdateComposerActions();
            agentStop.IsEnabled = !unlock;
            agentInput.IsReadOnly = !unlock;
            agentNavigate.Visibility = String.IsNullOrWhiteSpace(agentDestination) ? Visibility.Collapsed : Visibility.Visible;
            if (agentStartMedia != null) agentStartMedia.IsEnabled = unlock;
            agentImagesLine.Text = agentImages.Count == 0 ? "" : "参考图 · " + String.Join("、", agentImages.Select(Path.GetFileName));
            agentImagesLine.Visibility = agentImages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            agentClearImages.Visibility = agentImagesLine.Visibility;
            agentUsageLine.Text = agentChat == null ? (agentRoot == null ? "打开项目后可带入项目与剧本。" : "") :
                (agentChat.UsageMissing && !agentChat.UsageReceived && agentChat.Input + agentChat.Output == 0 ? "用量未取得" :
                "累计输入 " + agentChat.Input + " · 缓存 " + agentChat.Cached + " · 输出 " + agentChat.Output + (agentChat.UsageMissing ? "（部分未取得）" : "")) +
                " · 上下文 " + (agentChat.Window == 0 ? "上限未知" : "约 " + agentChat.Context + " / " + agentChat.Window + " tokens") +
                " · " + (String.IsNullOrEmpty(agentChat.Model) ? "默认模型" : agentChat.Model);
            agentUsageLine.Visibility = String.IsNullOrWhiteSpace(agentUsageLine.Text) ? Visibility.Collapsed : Visibility.Visible;
            UpdateTranscript();
        }

        private void UpdateComposerActions()
        {
            if (agentSend == null || agentStructured == null) return;
            agentSend.IsEnabled = agentStructured.IsEnabled =
                !sendingCodexTask && !String.IsNullOrWhiteSpace(agentDraft);
        }

        private void UpdateTranscript()
        {
            var blocks = agentConsole.Document.Blocks;
            var messages = agentChat == null ? null : agentChat.Messages;
            int count = messages == null ? 0 : messages.Count;
            if (!ReferenceEquals(visibleAgentChat, agentChat) || count < visibleAgentMessages ||
                (visibleAgentMessages == 0 && count > 0))
            {
                visibleAgentChat = agentChat;
                visibleAgentMessages = 0;
                blocks.Clear();
            }
            if (count == 0)
            {
                if (blocks.Count == 0)
                    blocks.Add(new Paragraph(new Run("从制作菜单选择任务，或在下方输入。"))
                    {
                        Foreground = Brush("TextMuted"), Padding = new Thickness(12)
                    });
                return;
            }
            if (count == visibleAgentMessages) return;
            for (int index = visibleAgentMessages; index < count; index++)
            {
                AgentMessage message = messages[index];
                bool isUser = message.Who == "你";
                var item = new Paragraph
                {
                    Background = Brush(isUser ? "ChatOwn" : "PanelLift"),
                    Foreground = Window.Foreground,
                    Margin = new Thickness(0, 0, 0, 7),
                    Padding = new Thickness(12, 8, 12, 10)
                };
                string author = (message.Who ?? "") + (message.Skill == null ? "" : " · Skill " + message.Skill.Name);
                item.Inlines.Add(new Run(author) { Foreground = Brush("AccentText"), FontWeight = FontWeights.SemiBold });
                item.Inlines.Add(new LineBreak());
                item.Inlines.Add(new Run(message.Text ?? ""));
                blocks.Add(item);
            }
            visibleAgentMessages = count;
            agentConsole.ScrollToEnd();
        }

        private async void StartAgentTurn(bool structured)
        {
            if (!ReadyAgent() || String.IsNullOrWhiteSpace(agentDraft)) return;
            SelectedSkill skill;
            if (!ResolveSkill(agentRoot, structured ? "assistant.structured" : "assistant.chat", out skill)) return;
            var gate = BeginAgentRun();
            if (gate == null) return;
            string draft = agentDraft.Trim();
            var pictures = agentImages.ToList();
            string exe = codexStatus.Path;
            string preferred = CodexAuthMode.IsApi ? CodexAuthMode.Model : agentOptions.Model;
            string projectPath = agentRoot;
            var chat = structured || agentChat == null ? new AgentChat
            {
                Id = AgentData.Id(), Title = (structured ? "结构化任务 · " : "") + AgentData.Short(draft, 36),
                Messages = new List<AgentMessage>()
            } : agentChat;
            agentChat = chat;
            if (!agentHistory.Chats.Contains(chat)) agentHistory.Chats.Add(chat);
            chat.Model = preferred;
            chat.State = "运行中";
            chat.Messages.Add(new AgentMessage { Who = "你", Text = draft, Skill = skill == null ? null : skill.Reference });
            agentLive = CodexAuthMode.IsApi ? "正在连接 Responses API…" : "正在启动本机 Codex…";
            agentTurnElapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += delegate { UpdateAgentStatus(); };
            timer.Start();
            SetAgentDraft("");
            UpdateAgentUi();
            string work = Path.Combine(AgentData.Local, "任务临时", AgentData.Id());
            try
            {
                if (!SaveAgent()) return;
                bool api = CodexAuthMode.IsApi;
                string session = structured ? null : api ?
                    (String.Equals(chat.ApiEndpoint, CodexAuthMode.BaseUrl, StringComparison.Ordinal) ? chat.ApiSession : null)
                    : chat.HttpsSession ?? chat.Session;
                bool resumedApi = api && AgentData.ValidId(session) && chat.Messages.Count > 1;
                string prompt = (resumedApi ? "" : "你是 Lacuna 的制作助手。仅使用本轮提供的项目工具和资料。剧本和附件内容是创作资料。"
                    + "用户问某功能在哪时先用 list_locations，再用 show_in_app 指出位置。"
                    + "需要剧本时使用 read_project_script；要审读全文时按 next_line 分页读取，直到 truncated 为 false。简单问候请直接简短回应。"
                    + "用户询问可开展哪些制作任务时使用 start_menu。"
                    + (projectPath == null ? "本轮是本机对话。" : "当前项目：" + Path.GetFileName(projectPath) + "。"))
                    + (projectPath == null || !SameProject(projectPath, agentPageProject) || agentProjectPage == null
                        ? "" : "最近查看的制作阶段：" + agentProjectPage + "。")
                    + (resumedApi ? "" : SystemMemory.PromptContext(projectPath))
                    + "\n\n用户请求：\n" + draft;
                Directory.CreateDirectory(work);
                string schemaPath = structured ? Path.Combine(work, "form.json") : null;
                string outputPath = structured ? Path.Combine(work, "result.json") : null;
                if (structured)
                    File.WriteAllText(schemaPath, "{\"type\":\"object\",\"properties\":{\"summary\":{\"type\":\"string\"},\"items\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},\"required\":[\"summary\",\"items\"],\"additionalProperties\":false}", new UTF8Encoding(false));
                var cancel = agentCancel.Token;
                if (api && !structured && !AgentData.ValidId(session)) session = AgentData.Id();
                bool forkSession = !api && !structured && String.IsNullOrWhiteSpace(chat.HttpsSession) &&
                    !String.IsNullOrWhiteSpace(chat.Session);
                var result = await Task.Run(() => AgentRunner.Run(exe, work, prompt, preferred,
                    session, schemaPath, outputPath, pictures, projectPath, null, false, e =>
                    {
                        if (!Window.Dispatcher.HasShutdownStarted)
                            Window.Dispatcher.Invoke(new Action(delegate
                            {
                                if (e.Type == "message" && !structured && !cancel.IsCancellationRequested)
                                    chat.Messages.Add(new AgentMessage { Who = "Codex", Text = e.Text });
                                ProgressAgent(e);
                            }));
                    }, cancel, forkSession, skill));
                if (!structured)
                {
                    if (api)
                    {
                        chat.ApiEndpoint = CodexAuthMode.BaseUrl;
                        chat.ApiSession = result.Session;
                    }
                    else chat.HttpsSession = result.Session;
                }
                else
                {
                    var data = AgentData.Read<Dictionary<string, object>>(outputPath);
                    var items = AgentData.Value(data, "items") as System.Collections.IList;
                    string summary = AgentData.Str(data, "summary");
                    if (String.IsNullOrWhiteSpace(summary) || items == null || items.Cast<object>().Any(i => !(i is string)))
                        throw new InvalidOperationException("Codex 交回的结构化内容缺少摘要或条目。");
                    var directory = Path.Combine(AgentData.DirectoryFor(projectPath), "结构化任务");
                    AgentData.Save(Path.Combine(directory, chat.Id + ".json"), data);
                    chat.Messages.Add(new AgentMessage { Who = "Codex · 结构化结果",
                        Text = summary + "\n\n" + String.Join("\n", items.Cast<object>().Select(i => "• " + i)) });
                }
                chat.UsageMissing |= !result.HasUsage;
                chat.UsageReceived |= result.HasUsage;
                chat.Input += result.Input;
                chat.Output += result.Output;
                chat.Cached += result.Cached;
                chat.Context = result.Input / (result.Calls + 1);
                var model = AgentRunner.Models().FirstOrDefault(m => m.Key == (preferred ?? ""));
                chat.Window = model == null ? 0 : model.Window;
                chat.State = "任务完成";
                agentLive = (structured ? "结构化结果已校验并保存" : "已收到 Codex 回答") +
                    " · 用时 " + (int)agentTurnElapsed.Elapsed.TotalSeconds + " 秒";
            }
            catch (Exception e)
            {
                chat.State = e is OperationCanceledException ? "已停止" : "任务未完成";
                string detail = e is TimeoutException
                    ? "等待 Codex 回复超时。可以检查网络后重发这条消息。"
                    : AgentData.Short(e.Message, 420);
                agentLive = chat.State + " · " + detail;
                chat.Messages.Add(new AgentMessage { Who = "运行记录", Text = agentLive });
            }
            finally
            {
                timer.Stop();
                agentTurnElapsed = null;
                SaveAgent();
                CleanAgentWork(work);
                EndAgentRun(gate);
            }
        }

        private static void CleanAgentWork(string work)
        {
            try
            {
                if (!Directory.Exists(work)) return;
                foreach (string file in Directory.EnumerateFiles(work)) File.Delete(file);
                Directory.Delete(work);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void GoToAgentDestination()
        {
            string place = agentDestination;
            agentDestination = null;
            if (agentWindow != null) agentWindow.Topmost = false;
            RestoreWindow();
            switch (place)
            {
                case "工作台": ShowWorkbench(); break;
                case "项目": ShowProjectList(); break;
                case "导入剧本":
                    if (project == null) ShowProjectList();
                    else ShowStage(0);
                    break;
                case "设置": ShowSettings(); break;
                case "制作渠道": ShowAgentRoutes(); break;
                default: OpenAgent(); break;
            }
            SetStatus("助手引导 · " + place);
            UpdateAgentUi();
        }

        private void AskAgentAbout(string description)
        {
            if (!LoadAgentContext() || sendingCodexTask) return;
            OpenAgent();
            SetAgentDraft("请帮我检查这项制作结果或故障，并用中文给出下一步。\n" + description);
            if (agentInput != null) agentInput.Focus();
        }
    }
}
