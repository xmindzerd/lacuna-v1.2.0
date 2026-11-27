using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private readonly ProjectStore store;
        private readonly StackPanel content;
        private readonly TextBlock status;
        private readonly Button[] navigation = new Button[4];
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private LocalPreferences preferences = new LocalPreferences();
        private ProjectProgress project;
        private string projectFolder;
        private string feedback;
        private bool exiting;
        internal static readonly List<Workspace> openWorkspaces = new List<Workspace>();

        private readonly FrameworkElement view;
        private readonly Workspace shell;
        private Workspace active;
        private bool IsCurrentView { get { return shell.active == this; } }
        private bool CanShowProjectDialog { get { return IsCurrentView && !shell.apiTesting && Window.IsVisible && Window.WindowState != WindowState.Minimized && !exiting && !exitAfterCodexTask; } }

        internal Window Window { get; private set; }

        internal Workspace(Window window, ProjectStore store) : this(window, store, null) { }

        private Workspace(Window window, ProjectStore store, Workspace owner)
        {
            Window = window;
            this.store = store;
            shell = owner ?? this;
            view = Program.Presentation<Grid>("WorkspaceView.xaml");
            if (owner == null)
            {
                active = this;
                Window.Content = view;
            }
            content = Find<StackPanel>("Workspace");
            status = Find<TextBlock>("Status");
            Find<Image>("BrandMark").Source = Program.Artwork("BrandMark.png");
            Find<System.Windows.Shapes.Rectangle>("BrandWordmark").OpacityMask = new ImageBrush(Program.Artwork("BrandWordmark.png")) { Stretch = Stretch.Uniform };
            Find<TextBlock>("Version").Text = Program.Version;
            var chip = Find<Border>("ProjectHeaderChip");
            Action openChip = () => { if (project != null) ShowOverview(); else ShowProjectList(); };
            chip.MouseLeftButtonUp += (sender,args) => { openChip(); args.Handled = true; };
            chip.KeyDown += (sender,args) => { if (args.Key == System.Windows.Input.Key.Enter || args.Key == System.Windows.Input.Key.Space) { openChip(); args.Handled = true; } };
            chip.MouseEnter += delegate { chip.BorderBrush = Brush("Focus"); };
            chip.MouseLeave += delegate { chip.BorderBrush = Brush("Line"); };
            Find<Button>("Exit").Click += delegate { RequestExit(); };
            if (owner == null)
            {
                Window.Title = "Lacuna · " + Program.Version;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("Brand.ico"))
                    Window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Window.SourceInitialized += delegate { SetTitleBarTheme(); };
                Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
                {
                    if (exiting) return;
                    args.Cancel = true;
                    HideToTray();
                };
                Window.Activated += delegate { active.OnShellActivated(); };
                Window.Closed += delegate
                {
                    DevMonitor.FlushOnClose();
                    if(taskPulse!=null){taskPulse.Stop();taskPulse=null;}
                    if(active.closeKeyEditor!=null)active.closeKeyEditor();
                    projectWindows.Clear();
                    openWorkspaces.Clear();
                    if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
                    if (trayIcon != null) trayIcon.Dispose();
                };
                Window.Loaded += delegate { active.RefreshCodex(); active.StartQuotaUpdates(); };
            }

            var sidebar = Find<StackPanel>("Navigation");
            navigation[0] = NavButton("\uE80F", "工作台", ShowWorkbench);
            navigation[1] = NavButton("\uE8B7", "项目", ShowProjectList);
            navigation[2] = NavButton("\uE713", "设置", ShowSettings);
            navigation[3] = NavButton("\uE8BD", "Codex 助手", OpenAgent);
            foreach (Button item in new[] { navigation[0], navigation[1], navigation[3], navigation[2] })
            {
                item.Margin = new Thickness(0, 0, 4, 0);
                sidebar.Children.Add(item);
            }

            TryAction(delegate
            {
                preferences = store.LoadPreferences();
                if (preferences.RecentProjects == null)
                    preferences.RecentProjects = new List<RecentProject>();
                if (!String.IsNullOrEmpty(preferences.LastProject) && preferences.RecentProjects.Count == 0)
                    preferences.RecentProjects.Add(new RecentProject { Folder = preferences.LastProject });
            });
            Find<Button>("CodexShortcut").Click += delegate { ShowSettings(); };
            ApplySystemDisplay();
            InitializeAgent();
            openWorkspaces.Add(this);
            if (owner == null) ShowWorkbench();
        }

        private void ShowStage(int index)
        {
            agentProjectPage = Stage.All[index].Name;
            agentPageProject = projectFolder;
            switch(index)
            {
                case 0: ShowProjectAnalysis(); break;
                case 1: ShowAssetHandoff(); break;
                case 5: ShowVideoJobs(); break;
                case 6: ShowDelivery(); break;
                default: ShowFilm(index); break;
            }
        }

        private FrameworkElement DialogueLanguagePicker(string selected, Action<string> change)
        {
            var panel = new StackPanel();
            panel.Children.Add(Muted("短剧对白语言", 14, 10));
            var picker = new ComboBox { MinHeight = 35, ItemsSource = new[] { "中文短剧", "英文短剧" },
                SelectedIndex = selected == "英文" ? 1 : 0, Margin = new Thickness(0,4,0,6) };
            NameControl(picker, "短剧对白语言");
            picker.SelectionChanged += delegate { change(picker.SelectedIndex == 1 ? "英文" : "中文"); };
            panel.Children.Add(picker);
            panel.Children.Add(StudioText("双语对白按所选语言制作。原稿保留，成片仅使用所选语言的对白和画外音。",13,1));
            return panel;
        }

        private void OpenProject()
        {
            string folder = PickFolder("选择已有的项目文件夹", preferences.Root);
            if (folder != null) TryAction(() => ProjectWindow(folder).ShowOverview());
        }

        private bool ChooseLocation()
        {
            string initial = String.IsNullOrEmpty(preferences.Root)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(preferences.Root);
            string folder = PickFolder("选择 Lacuna 的项目保存位置", initial);
            if (folder == null)
                return false;
            var updated = preferences.Copy();
            updated.Root = store.SelectRoot(folder);
            store.SavePreferences(updated);
            preferences = updated;
            return true;
        }

        private static readonly Dictionary<string, Workspace> projectWindows =
            new Dictionary<string, Workspace>(StringComparer.OrdinalIgnoreCase);

        private Workspace ProjectWindow(string folder, string replaced = null)
        {
            string path = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            store.OpenProject(path);
            Workspace target;
            if (!projectWindows.TryGetValue(path, out target))
            {
                target = projectFolder == null && !sendingCodexTask && flowCreating == null && assetTasks.Count == 0
                    && !systemBusy && installCancel == null && agentWindow == null ? this : new Workspace(Window, store, shell);
                target.exitAfterCodexTask = exitAfterCodexTask;
                try { target.LoadProject(path, replaced); }
                catch
                {
                    if (target != this) openWorkspaces.Remove(target);
                    throw;
                }
                projectWindows.Add(path, target);
            }
            else target.LoadProject(path, replaced);

            Workspace previous = shell.active;
            if (previous != target && previous.agentWindow != null) previous.agentWindow.Hide();
            shell.active = target;
            Window.Content = target.view;
            if(FlowCreation.Development)DevMonitor.Action(folder,"打开项目「"+target.DisplayName()+"」");
            Window.Title = target.project.Name + " · Lacuna · " + Program.Version;
            target.agentOptions = AgentData.Options();
            target.ApplySystemDisplay();
            if (target.codexStatus == null && !target.checkingCodex)
            {
                target.codexStatus = previous.codexStatus;
                if (target.codexStatus == null) target.RefreshCodex();
                else target.UpdateCodexDisplay();
            }
            shell.DrawProjectNotices();
            return target;
        }

        private void LoadProject(string folder, string replacedFolder = null)
        {
            folder = Path.GetFullPath(folder);
            ProjectProgress opened = store.OpenProject(folder);
            if (!SameProject(folder,projectFolder)) { reviewEpisode=null; assetFilter="全部"; filmDetailPage=-1; }
            var updated = store.LoadPreferences();
            updated.LastProject = folder;
            updated.RecentProjects = new List<RecentProject>(updated.RecentProjects ?? new List<RecentProject>());
            updated.RecentProjects.RemoveAll(entry => entry == null || String.IsNullOrEmpty(entry.Folder)
                || SameFolder(entry.Folder, folder) || SameFolder(entry.Folder, replacedFolder));
            updated.RecentProjects.Insert(0, new RecentProject { Folder = folder, Name = opened.Name });
            if (updated.RecentProjects.Count > RecentLimit)
                updated.RecentProjects.RemoveRange(RecentLimit, updated.RecentProjects.Count - RecentLimit);
            store.SavePreferences(updated);
            preferences = updated;
            project = opened;
            projectFolder = folder;
        }

        private string scriptImportProject;
        private System.Threading.CancellationTokenSource scriptImportCancel;
        private int pageRevision;

        private async void ImportScript()
        {
            if (project == null)
            {
                SetStatus("请先新建或打开项目。");
                return;
            }
            if (systemBusy || sendingCodexTask)
            {
                SetStatus("请等待当前项目操作结束后再导入剧本。");
                return;
            }
            var chooser = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择剧本文件",
                Filter = "剧本文件（PDF、TXT、Markdown、Word）|*.pdf;*.txt;*.md;*.docx|PDF 文件|*.pdf",
                CheckFileExists = true
            };
            if (chooser.ShowDialog(Window) != true) return;
            string folder = projectFolder, original = chooser.FileName;
            var cancellation = new System.Threading.CancellationTokenSource();
            var token = cancellation.Token;
            scriptImportCancel = cancellation;
            scriptImportProject = folder;
            systemBusy = true;
            SetStatus("正在导入剧本并读取正文…");
            ShowProjectAnalysis();
            int page = pageRevision;
            string message;
            try
            {
                var progress = await System.Threading.Tasks.Task.Run(delegate
                {
                    using (AnalysisData.Lock(folder))
                    {
                        return store.ImportScript(folder, original, copied =>
                        {
                            if (PdfScript.IsPdf(copied)) PdfScript.Prepare(copied, text =>
                            {
                                Window.Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    if (scriptImportCancel == cancellation && !token.IsCancellationRequested && SameProject(projectFolder, folder))
                                        SetStatus(text);
                                }));
                            }, token);
                            else ScriptDocument.Read(copied, null);
                            token.ThrowIfCancellationRequested();
                        });
                    }
                });
                if (SameProject(projectFolder, folder)) project = progress;
                message = "剧本已导入，可以查阅正文或开始分析。";
            }
            catch (Exception error)
            {
                message = token.IsCancellationRequested ? "导入已取消。" : "剧本导入未完成。 " + AgentData.Short(error.Message, 400);
            }
            finally
            {
                scriptImportProject = null;
                scriptImportCancel = null;
                cancellation.Dispose();
                systemBusy = false;
            }
            if (SameProject(projectFolder, folder))
            {
                if (pageRevision == page) ShowProjectAnalysis();
                SetStatus(message);
            }
        }

        private void ShowProjectFolder()
        {
            if (projectFolder == null)
            {
                SetStatus("请先新建或打开项目。");
                return;
            }
            TryAction(delegate
            {
                if (!Directory.Exists(projectFolder))
                    throw new InvalidOperationException("找不到当前项目文件夹，请重新打开项目。");
                using (Process.Start(new ProcessStartInfo(projectFolder) { UseShellExecute = true })) { }
            });
        }

        private void HideToTray()
        {
            if (tray == null)
            {
                var menu = new Forms.ContextMenuStrip
                {
                    BackColor = System.Drawing.Color.FromArgb(34, 36, 49),
                    ForeColor = System.Drawing.Color.FromArgb(241, 241, 248),
                    ShowImageMargin = false,
                    Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors())
                };
                menu.Items.Add("显示 Lacuna", null, delegate { RestoreWindow(); });
                menu.Items.Add("退出", null, delegate { RestoreWindow(); active.RequestExit(); });
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("Brand.ico"))
                using (var icon = new System.Drawing.Icon(stream, 32, 32))
                    trayIcon = (System.Drawing.Icon)icon.Clone();
                tray = new Forms.NotifyIcon
                {
                    Icon = trayIcon,
                    Text = "Lacuna " + Program.Version,
                    ContextMenuStrip = menu
                };
                tray.DoubleClick += delegate { RestoreWindow(); };
            }
            tray.Visible = true;
            Window.Hide();
        }

        private sealed class TrayColors : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Panel = System.Drawing.Color.FromArgb(34, 36, 49);
            private static readonly System.Drawing.Color Highlight = System.Drawing.Color.FromArgb(48, 51, 72);
            private static readonly System.Drawing.Color Divider = System.Drawing.Color.FromArgb(55, 58, 73);
            public override System.Drawing.Color ToolStripDropDownBackground { get { return Panel; } }
            public override System.Drawing.Color MenuItemSelected { get { return Highlight; } }
            public override System.Drawing.Color MenuBorder { get { return Divider; } }
            public override System.Drawing.Color MenuItemBorder { get { return Divider; } }
        }

        private void RestoreWindow()
        {
            if (shell.tray != null) shell.tray.Visible = false;
            Window.Show();
            if (Window.WindowState == WindowState.Minimized)
                Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        private void RequestExit()
        {
            if (exitAfterCodexTask) return;
            if (openWorkspaces.Exists(w => w.systemBusy))
            {
                SetStatus("正在执行系统资料或导入操作，请稍候完成后再退出。");
                return;
            }
            bool anyRunning = openWorkspaces.Exists(w => w.sendingCodexTask || w.assetTasks.Count != 0);
            string unused;
            string message = anyRunning ? "当前 Codex 任务结束后，软件将保存并退出。" : "当前项目资料已随操作保存。";
            if (!Prompt("退出 Lacuna", message,
                "保存进度并退出", "继续使用", false, out unused))
                return;
            if (anyRunning)
            {
                foreach (Workspace ws in openWorkspaces) ws.exitAfterCodexTask = true;
                shell.CancelPendingNotices();
                SetStatus("正在等待当前 Codex 任务完成后退出。");
                return;
            }
            foreach (Workspace ws in openWorkspaces.ToArray())
            {
                if (ws.agentSaveFailed && !ws.SaveAgent()) return;
            }
            FinishExit();
        }

        private void FinishExit()
        {
            if (openWorkspaces.Exists(w => w.assetTasks.Count > 0 || w.sendingCodexTask || w.systemBusy))
            {
                foreach (Workspace ws in openWorkspaces) ws.exitAfterCodexTask = true;
                return;
            }
            ExitForSystem();
        }

        private sealed class ProjectNotice
        {
            internal string Label;
            internal string Folder;
            internal Action Open, Dismiss;
        }
        private readonly List<ProjectNotice> notices = new List<ProjectNotice>();
        private bool decisionShowing;

        private void DrawProjectNotices()
        {
            var owner = shell.active;
            var panel = owner.Find<StackPanel>("ProjectNotices");
            panel.Children.Clear();
            var host = panel.Parent as FrameworkElement;
            if (host != null) host.Visibility = shell.notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (shell.notices.Count == 0) return;
            panel.Margin = new Thickness(22, 8, 22, 4);
            foreach (var item in shell.notices)
            {
                bool pending = item.Label.Contains("等待确认");
                var row = new DockPanel { LastChildFill = true };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                var openBtn = owner.ActionButton("查看", item.Open, pending);
                openBtn.Padding = new Thickness(12, 5, 12, 5);
                openBtn.MinHeight = 30;
                openBtn.Margin = new Thickness(0, 0, 6, 0);
                var dismissBtn = owner.ActionButton(pending ? "取消本次请求" : "关闭通知", item.Dismiss, false);
                dismissBtn.Padding = new Thickness(10, 5, 10, 5);
                dismissBtn.MinHeight = 30;
                dismissBtn.Margin = new Thickness(0);
                buttons.Children.Add(openBtn);
                buttons.Children.Add(dismissBtn);
                AddRight(row, buttons);

                var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var badge = owner.StatusBadge(pending ? "待确认" : "已完成", pending ? "Review" : "Accent");
                badge.Margin = new Thickness(0, 0, 10, 0);
                info.Children.Add(badge);
                var label = Text(item.Label, 13, 0);
                label.TextWrapping = TextWrapping.Wrap;
                label.Foreground = owner.Brush("TextMain");
                info.Children.Add(label);
                row.Children.Add(info);

                var card = new Border
                {
                    Background = owner.Brush("PanelLift"),
                    BorderBrush = owner.Brush(pending ? "Review" : "Accent"),
                    BorderThickness = new Thickness(3, 1, 1, 1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 7, 12, 7),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = row
                };
                panel.Children.Add(card);
            }
        }

        private void SelectDecisionOwner()
        {
            if (IsCurrentView) return;
            if (projectFolder != null) { shell.active.ProjectWindow(projectFolder); return; }
            if (shell.active.agentWindow != null) shell.active.agentWindow.Hide();
            shell.active = this;
            Window.Content = view;
            Window.Title = "Lacuna · " + Program.Version;
            shell.DrawProjectNotices();
        }

        private System.Threading.Tasks.Task<int> ProjectDecision(string ownerName, string title, Func<bool> onPage,
            Action reopen, Func<int> choose, System.Threading.CancellationToken token)
        {
            if (!Window.Dispatcher.CheckAccess())
                return Window.Dispatcher.Invoke(new Func<System.Threading.Tasks.Task<int>>(
                    () => ProjectDecision(ownerName, title, onPage, reopen, choose, token)));
            var completion = new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new ProjectNotice { Folder = projectFolder, Label = "等待确认 · " + ownerName + " · " + title };
            System.Threading.CancellationTokenRegistration registration = default(System.Threading.CancellationTokenRegistration);
            Action remove = delegate
            {
                registration.Dispose();
                shell.notices.Remove(item);
                shell.DrawProjectNotices();
            };
            item.Dismiss = () => { remove(); completion.TrySetResult(-1); };
            item.Open = delegate
            {
                if (completion.Task.IsCompleted) return;
                if (token.IsCancellationRequested || exitAfterCodexTask || exiting) { item.Dismiss(); return; }
                try
                {
                    SelectDecisionOwner();
                    if (!onPage()) reopen();
                    if (!CanShowProjectDialog || !onPage() || shell.decisionShowing) return;
                    int choice;
                    shell.decisionShowing = true;
                    try { choice = choose(); }
                    finally { shell.decisionShowing = false; }
                    remove();
                    completion.TrySetResult(token.IsCancellationRequested || exitAfterCodexTask ? -1 : choice);
                }
                catch (Exception error) { remove(); completion.TrySetException(error); }
            };
            if (token.IsCancellationRequested || exitAfterCodexTask || exiting) { completion.SetResult(-1); return completion.Task; }
            shell.notices.Add(item);
            registration = token.Register(() => Window.Dispatcher.BeginInvoke(new Action(() => {
                if (!completion.Task.IsCompleted) item.Dismiss();
            })));
            shell.DrawProjectNotices();
            if (CanShowProjectDialog && !shell.decisionShowing && onPage()) item.Open();
            return completion.Task;
        }

        private void CancelPendingNotices()
        {
            foreach (var item in shell.notices.ToArray()) item.Dismiss();
        }

        private void NotifyCreated(string folder, string name)
        {
            var item = new ProjectNotice { Folder = folder, Label = "项目已创建 · " + name };
            item.Dismiss = () => { shell.notices.Remove(item); shell.DrawProjectNotices(); };
            item.Open = () => { if (shell.active.TryAction(() => shell.active.ProjectWindow(folder).ShowFlowProject())) item.Dismiss(); };
            shell.notices.Add(item);
            shell.DrawProjectNotices();
        }

        private Window StyleDialog(Window dialog)
        {
            dialog.FontFamily = Window.FontFamily;
            dialog.Foreground = Window.Foreground;
            dialog.Icon = Window.Icon;
            dialog.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(dialog, TextRenderingMode.Grayscale);
            dialog.SourceInitialized += delegate { SetTitleBarTheme(dialog); };
            UiFx.AnimateDialogOpen(dialog, Sys);
            return dialog;
        }

        internal static BitmapImage LoadBitmapFile(string path, int decodePixelWidth = 0)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        internal static Uri FileUri(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            var parts = full.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == 0 && parts[i].Length == 2 && parts[i][1] == ':') continue;
                parts[i] = Uri.EscapeDataString(parts[i]);
            }
            return new Uri("file:///" + String.Join("/", parts));
        }

        private Border StatusBadge(string text, string tone = "Default")
        {
            string bgKey = tone == "Accent" ? "SelectedNavigation" : tone == "Review" ? "PanelLift" : "PanelLift";
            string borderKey = tone == "Accent" ? "Accent" : tone == "Review" ? "Review" : "Line";
            string fgKey = tone == "Accent" ? "AccentText" : tone == "Review" ? "Review" : "TextMuted";
            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = tone == "Default" ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = Brush(fgKey),
                VerticalAlignment = VerticalAlignment.Center
            };
            return new Border
            {
                Background = Brush(bgKey),
                BorderBrush = Brush(borderKey),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = label
            };
        }

        private bool Prompt(string title, string message, string accept, string cancel, bool withInput, out string value,
            string inputLabel = "项目名称", string preview = null, FrameworkElement extra = null)
        {
            if (!IsCurrentView) { value = null; return false; }
            var form = StyleDialog(new Window
            {
                Owner = agentWindow != null && agentWindow.IsActive ? agentWindow : Window,
                Title = title,
                Width = preview == null ? 500 : 680,
                SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height - 48,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brush("Panel")
            });
            var body = new StackPanel { Margin = new Thickness(24) };
            body.Children.Add(Title(title, 22));
            var description = Muted(message, 14, 10);
            description.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(new ScrollViewer
            {
                Content = description, MaxHeight = preview == null ? 380 : 185,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            if (preview != null)
            {
                var previewText = new TextBox { Text = preview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 185, Margin = new Thickness(0, 6, 0, 0) };
                body.Children.Add(new Expander { Header = "逐项查看本次提交的提示词", Foreground = Window.Foreground,
                    Content = previewText, Margin = new Thickness(0, 12, 0, 0) });
            }
            TextBox input = null;
            if (withInput)
            {
                body.Children.Add(Muted(inputLabel, 13, 14));
                input = new TextBox { Margin = new Thickness(0, 6, 0, 0), MinHeight = 40 };
                AutomationProperties.SetName(input, inputLabel);
                body.Children.Add(input);
                form.Loaded += delegate { input.Focus(); };
            }
            if (extra != null) body.Children.Add(extra);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var cancelButton = ActionButton(cancel, delegate { form.DialogResult = false; }, false);
            cancelButton.IsCancel = true;
            actions.Children.Add(cancelButton);
            var acceptButton = ActionButton(accept, delegate { form.DialogResult = true; }, true);
            acceptButton.IsDefault = withInput;
            acceptButton.Margin = new Thickness(0, 2, 0, 4);
            actions.Children.Add(acceptButton);
            body.Children.Add(actions);
            form.Content = new Border
            {
                Background = Brush("Panel"),
                Child = new ScrollViewer
                {
                    Content = body,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
            bool accepted = form.ShowDialog() == true;
            value = input == null ? null : input.Text;
            return accepted;
        }

        private string PickFolder(string description, string initial)
        {
            using (var picker = new Forms.FolderBrowserDialog())
            {
                picker.Description = description;
                if (!String.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    picker.SelectedPath = initial;
                return picker.ShowDialog(new DialogOwner(new WindowInteropHelper(Window).Handle)) == Forms.DialogResult.OK
                    ? picker.SelectedPath : null;
            }
        }

        private sealed class DialogOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            internal DialogOwner(IntPtr handle) { Handle = handle; }
        }

        internal void CopyText(string value, string notice = null)
        {
            TryAction(delegate
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetText(value ?? ""); break; }
                    catch (ExternalException)
                    {
                        if (attempt >= 3) throw new IOException("Windows 剪贴板正被其他程序占用，请稍后再试。");
                        System.Threading.Thread.Sleep(25);
                    }
                }
                if (notice != null) SetStatus(notice);
            });
        }

        private bool TryAction(Action action)
        {
            if(shell.apiTesting)return false;
            try
            {
                feedback = null;
                action();
                RefreshStatus();
                return true;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is InvalidOperationException || error is ArgumentException ||
                    error is NotSupportedException || error is FormatException ||
                    error is ExternalException || error is System.ComponentModel.Win32Exception))
                    throw;
                SetStatus(error.Message);
                return false;
            }
        }

        private bool ScriptAvailable()
        {
            return project != null && !String.IsNullOrEmpty(project.Script)
                && File.Exists(store.ProjectFile(projectFolder, project.Script));
        }

        private int currentStageSlot = -1;
        private Action cleanupPageAnimations;

        private void BeginPage(int selected, string heading, string description, int stageSlot = -1)
        {
            if(closeKeyEditor!=null)closeKeyEditor();
            if(cleanupPageAnimations!=null){try{cleanupPageAnimations();}catch{}cleanupPageAnimations=null;}
            flowStatusHeader=null;
            if(FlowCreation.Development && heading!=null)DevMonitor.Action(projectFolder,"打开"+heading);
            pageRevision++;
            revalidateQuote=null;
            refreshPickers=null;
            if(quoteWork!=null){quoteWork.Cancel();quoteWork=null;}
            switchAnalysisTab=null;
            CancelScriptReaderWork();
            analysisProgress = null;
            for (int index = 0; index < navigation.Length; index++)
                navigation[index].Tag = index == selected ? "Active" : null;
            codexDetails = null;
            content.Children.Clear();
            currentStageSlot = stageSlot;
            PaintStageRail(selected);
            UiFx.AnimatePageEntrance(content, Sys);
            var footer = Find<ContentControl>("PageFooter");
            footer.Content = null;
            footer.Visibility = Visibility.Collapsed;
            if (heading != null)
            {
                var headerBlock = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                var title = Text(SystemWord(heading), 28, 0);
                title.FontWeight = FontWeights.SemiBold;
                headerBlock.Children.Add(title);
                if (description != null)
                    headerBlock.Children.Add(Muted(description, 13.5, 6));
                content.Children.Add(headerBlock);
            }
            Find<ScrollViewer>("WorkspaceScroll").ScrollToTop();
            RefreshStatus();
        }

        private void SetStatus(string message)
        {
            DevMonitor.Progress(projectFolder,message);
            feedback = message;
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            string location = String.IsNullOrEmpty(preferences.Root)
                ? "尚未设置保存位置" : "保存位置  " + preferences.Root;
            string name = DisplayName();
            string locationStatus = String.IsNullOrEmpty(preferences.Root) ? location : "已设置项目保存位置";
            string context = project == null ? locationStatus : "当前项目  " + name;
            Find<Border>("ProjectHeaderChip").ToolTip = name == null ? "点击打开项目列表" : "点击返回当前项目总览";
            Find<TextBlock>("ProjectHeader").Text = name ?? "未打开项目";
            var projectDot = view.FindName("ProjectDot") as System.Windows.Shapes.Ellipse;
            if (projectDot != null)
                projectDot.Fill = Brush(name != null ? "Accent" : "Line");
            var producing = assetTasks.Find(t => t.Name == FlowTaskName && SameProject(t.Root, projectFolder));
            var running=producing ?? assetTasks.FirstOrDefault(t=>SameProject(t.Root,projectFolder)) ?? (projectFolder==null?assetTasks.FirstOrDefault():null);
            status.Text=running!=null ? (project==null?running.Name:project.Name)+" · "+running.TimedStatus : feedback ?? context;
            status.ToolTip = feedback ?? (projectFolder == null ? location : projectFolder + "\n" + location);
            bool activeTask = assetTasks.Count > 0 || sendingCodexTask || systemBusy;
            status.Foreground = feedback != null && !activeTask ? Brush("Review") : Brush("TextMuted");
            var statusDot = view.FindName("StatusDot") as System.Windows.Shapes.Ellipse;
            if (statusDot != null)
            {
                statusDot.Fill = Brush(activeTask ? "Accent" : (feedback != null ? "Review" : "ScrollThumb"));
                UiFx.SetPulse(statusDot, activeTask, Sys);
            }
        }

        private Button NavButton(string mark, string label, Action action)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock icon = Icon(mark, 15);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
            row.Children.Add(Text(label, 14, 0));
            var button = ActionButton(row, action, false);
            button.Style = Style("NavButton");
            AutomationProperties.SetName(button, label);
            return button;
        }

        private Button ActionButton(object label, Action action, bool primary)
        {
            var button = new Button
            {
                Content = label is string ? SystemWord((string)label) : label,
                Style = primary ? Style("PrimaryButton") : (Style)Window.FindResource(typeof(Button))
            };
            button.Click += delegate {
                if(shell.apiTesting)return;
                if(FlowCreation.Development){
                    string name=label as string ?? AutomationProperties.GetName(button);
                    if(!String.IsNullOrEmpty(name)) DevMonitor.Action(projectFolder,name);
                }
                action();
            };
            if (label is string)
                button.Margin = new Thickness(0, 2, 8, 4);
            return button;
        }

        private Border Panel(UIElement child)
        {
            return new Border
            {
                Background = Brush("Panel"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Child = child
            };
        }

        private Border Line()
        {
            return new Border { Height = 1, Background = Brush("Line") };
        }

        private static TextBlock Text(string value, double size, double top)
        {
            return new TextBlock
            {
                Text = value, FontSize = size, Margin = new Thickness(0, top, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private TextBlock Muted(string value, double size, double top)
        {
            var text = Text(value, size, top);
            text.Foreground = Brush("TextMuted");
            return text;
        }

        private static TextBlock Icon(string glyph, double size)
        {
            var text = Text(glyph, size, 0);
            text.FontFamily = new FontFamily("Segoe MDL2 Assets");
            return text;
        }

        private static void AddLeft(DockPanel panel, UIElement element)
        {
            DockPanel.SetDock(element, Dock.Left);
            panel.Children.Add(element);
        }

        private static void AddRight(DockPanel panel, UIElement element)
        {
            DockPanel.SetDock(element, Dock.Right);
            panel.Children.Add(element);
        }

        private static string Number(int index) { return (index + 1).ToString("00"); }
        private T Find<T>(string name) where T : FrameworkElement { return (T)view.FindName(name); }
        private Brush Brush(string key) { return (Brush)Window.FindResource(key); }
        private Style Style(string key) { return (Style)Window.FindResource(key); }

        private void SetTitleBarTheme() { SetTitleBarTheme(Window); }

        private static void SetTitleBarTheme(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            int dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            // Windows 11 uses COLORREF (red in the low byte) for caption colors.
            int border = 0x00493A37;
            int caption = 0x00221917;
            int text = 0x00F8F1F1;
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}

