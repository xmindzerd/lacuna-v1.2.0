using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private bool systemBusy;

        private SystemOptions Sys
        {
            get { return preferences.System ?? (preferences.System = new SystemOptions()); }
        }

        private static readonly IDictionary<string, string> PlainTerms = new Dictionary<string, string>
        {
            { "剧本导入与分析", "看剧本" }, { "分析剧本", "分析剧本内容" },
            { "资产制作与自检", "做参考图" }, { "分镜", "设计镜头" }, { "分段", "划分视频片段" },
            { "阶段审核", "检查并确认" }, { "成片交付", "导出成片" },
            { "视频提示词", "写视频要求" }, { "结构化任务", "生成清单" },
            { "制作渠道", "选择图片和视频工具" }, { "连接其它助手", "给其他助手接入项目" },
            { "核对原平台任务", "查看之前已提交的制作" }
        };

        private string SystemWord(string words)
        {
            string label;
            return words != null && Sys.PlainWords && PlainTerms.TryGetValue(words, out label) ? label : words;
        }

        private void ApplySystemDisplay()
        {
            string folder = SystemData.Active.Folder;
            if (Sys.BoundFolder != null && !SystemData.Same(Sys.BoundFolder, folder))
            {
                TryAction(delegate
                {
                    var move = AgentData.Read<LibraryReceipt>(ReceiptPath(folder));
                    if (!Directory.Exists(Sys.BoundFolder) || (move != null &&
                        SystemData.Same(move.Source, Sys.BoundFolder) && SystemData.Same(move.Destination, folder)))
                        SystemData.RemapProjects(preferences, Sys.BoundFolder, folder);
                });
            }
            Sys.BoundFolder = folder;

            bool high = Sys.HighContrast;
            string[] accent = Sys.Accent == "teal" ? new[] { "#88E5CF", "#B7F5E6", "#233B38", "#72C9B4", "#477D71" } :
                Sys.Accent == "rose" ? new[] { "#F5ACC8", "#FFD2E3", "#3F2634", "#DE92B2", "#955673" } :
                Sys.Accent == "amber" ? new[] { "#FFAA82", "#FFC4A2", "#34352E", "#F3AF86", "#835C49" } :
                new[] { "#B9B0FC", "#CEC7FF", "#28263B", "#AAA1EC", "#776DB3" };
            SetBrush("Accent", high ? "#FFDB57" : accent[0]);
            SetBrush("AccentText", high ? "#FFEA9A" : accent[1]);
            SetBrush("SelectedNavigation", high ? "#332A00" : accent[2]);
            SetBrush("Focus", high ? "#FFFFFF" : accent[3]);
            SetBrush("Selection", high ? "#756022" : accent[4]);
            SetBrush("KeyboardFocus", high || Sys.StrongFocus ? "#FFDE59" : accent[3]);
            SetBrush("AppBackground", high ? "#000000" : "#12131A");
            SetBrush("TopBackground", high ? "#000000" : "#151720");
            SetBrush("SideBackground", high ? "#000000" : "#171922");
            SetBrush("TextMain", high ? "#FFFFFF" : "#F1F1F8");
            SetBrush("TextMuted", high ? "#F2F2F2" : "#AFB1C3");
            SetBrush("Panel", high ? "#080808" : "#191B24");
            SetBrush("PanelLift", high ? "#141414" : "#222431");
            SetBrush("Input", high ? "#000000" : "#12141D");
            SetBrush("Line", high ? "#D1D1D1" : "#373A49");
            SetBrush("Hover", high ? "#292929" : "#303348");
            SetBrush("ScrollThumb", high ? "#BFBFBF" : "#4B4E66");
            SetBrush("ChatOwn", high ? "#252525" : "#252435");
            SetBrush("Review", high ? "#FFE072" : "#F7B867");
            Application.Current.Resources["KeyboardThickness"] =
                new Thickness(high || Sys.StrongFocus ? 3 : 1);
            Application.Current.Resources["PopupMotion"] = Sys.ReduceMotion || !SystemParameters.MenuAnimation
                ? PopupAnimation.None : PopupAnimation.Fade;
            content.LayoutTransform = SystemScale();
            if (agentWindow != null && agentPanel != null)
            {
                agentWindow.SetResourceReference(Control.BackgroundProperty, "AppBackground");
                agentWindow.SetResourceReference(Control.ForegroundProperty, "TextMain");
                agentPanel.LayoutTransform = SystemScale();
            }
        }

        private static void SetBrush(string key, string color)
        {
            var resources = Application.Current.Resources;
            var value = (Color)ColorConverter.ConvertFromString(color);
            var brush = resources[key] as SolidColorBrush;
            if (brush != null && !brush.IsFrozen) brush.Color = value;
            else resources[key] = new SolidColorBrush(value);
        }

        private ScaleTransform SystemScale()
        {
            var scale = (Sys.Zoom == 115 || Sys.Zoom == 130 ? Sys.Zoom : 100) / 100.0;
            return new ScaleTransform(scale, scale);
        }

        private void GoRecent(bool scriptFirst)
        {
            if (projectFolder != null && project != null && store.HasProgress(projectFolder))
            {
                if (scriptFirst) ShowStage(0);
                else OpenProjectAction();
                return;
            }
            if (!String.IsNullOrEmpty(preferences.LastProject) && store.HasProgress(preferences.LastProject))
            {
                TryAction(delegate {
                    var target = ProjectWindow(preferences.LastProject);
                    if (scriptFirst) target.ShowStage(0); else target.OpenProjectAction();
                });
            }
            else ShowProjectList();
        }

        private void AddSystemSettings()
        {
            var actions = SystemActions();
            actions.Children.Add(ActionButton("显示与用语", ShowSystemDisplay, false));
            actions.Children.Add(ActionButton("资料与输出位置", ShowSystemStorage, false));
            actions.Children.Add(ActionButton("问题反馈", ShowFeedbackCenter, false));
            actions.Children.Add(ActionButton("版本与诊断", ShowSystemVersion, false));
            actions.Children.Add(ActionButton("共享项目记忆", ShowSystemMemory, false));
            content.Children.Add(actions);
        }

        private StackPanel SystemSection(string heading)
        {
            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(Title(heading, 19));
            var panel = Panel(stack);
            panel.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(panel);
            return stack;
        }

        private static WrapPanel SystemActions()
        {
            return new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        }

        private void SystemDetail(StackPanel section, string text)
        {
            var information = Muted(text, 13, 8);
            information.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(information);
        }

        private void SaveSystemOptions(SystemOptions options)
        {
            var updated = preferences.Copy();
            updated.System = options;
            store.SavePreferences(updated);
            preferences = updated;
        }

        private void ChangeDisplay(Action<SystemOptions> change)
        {
            if (!TryAction(delegate
            {
                var options = Sys.Copy();
                change(options);
                SaveSystemOptions(options);
            })) return;
            ApplySystemDisplay();
            ShowSystemDisplay();
        }

        private void ShowSystemDisplay()
        {
            BeginPage(2, "显示与用语", null);
            AddPageBack("返回设置", ShowSettings);
            var section = SystemSection("主题强调色");
            SystemDetail(section, "当前 · " + (Sys.Accent == "teal" ? "青绿" : Sys.Accent == "rose" ? "暖莓" : Sys.Accent == "amber" ? "片场琥珀" : "经典紫"));
            var accents = SystemActions();
            accents.Children.Add(ActionButton("片场琥珀", delegate { ChangeDisplay(o => o.Accent = "amber"); }, false));
            accents.Children.Add(ActionButton("经典紫", delegate { ChangeDisplay(o => o.Accent = "violet"); }, false));
            accents.Children.Add(ActionButton("青绿", delegate { ChangeDisplay(o => o.Accent = "teal"); }, false));
            accents.Children.Add(ActionButton("暖莓", delegate { ChangeDisplay(o => o.Accent = "rose"); }, false));
            section.Children.Add(accents);

            section = SystemSection("界面用语");
            SystemDetail(section, "当前 · " + (Sys.PlainWords ? "简明用语" : "标准用语"));
            var words = SystemActions();
            words.Children.Add(ActionButton("标准用语", delegate { ChangeDisplay(o => o.PlainWords = false); }, false));
            words.Children.Add(ActionButton("简明用语", delegate { ChangeDisplay(o => o.PlainWords = true); }, false));
            section.Children.Add(words);

            section = SystemSection("阅读与键盘");
            SystemDetail(section, "工作区大小 · " + (Sys.Zoom == 0 ? 100 : Sys.Zoom) + "%。用 Tab 在按钮和输入框间移动，按 Enter 操作。");
            var scaling = SystemActions();
            foreach (int n in new[] { 100, 115, 130 })
            {
                int size = n;
                scaling.Children.Add(ActionButton(n + "%", delegate { ChangeDisplay(o => o.Zoom = size); }, false));
            }
            section.Children.Add(scaling);
            var accessibility = SystemActions();
            accessibility.Children.Add(ActionButton("高对比 · " + (Sys.HighContrast ? "已开启" : "已关闭"),
                delegate { ChangeDisplay(o => o.HighContrast = !o.HighContrast); }, false));
            accessibility.Children.Add(ActionButton("醒目键盘焦点 · " + (Sys.StrongFocus ? "已开启" : "已关闭"),
                delegate { ChangeDisplay(o => o.StrongFocus = !o.StrongFocus); }, false));
            accessibility.Children.Add(ActionButton("降低动效 · " + (Sys.ReduceMotion ? "已开启" : "已关闭"),
                delegate { ChangeDisplay(o => o.ReduceMotion = !o.ReduceMotion); }, false));
            section.Children.Add(accessibility);
        }

        private void ShowSystemStorage()
        {
            BeginPage(2, "资料与输出位置", null);
            AddPageBack("返回设置", ShowSettings);
            var section = SystemSection("项目、素材与生成产物");
            SystemDetail(section, "项目库 · " + (preferences.Root ?? "未设置") +
                "\n项目素材和已核验的图片、视频随各自项目保存。选择新位置会影响新建项目。");
            var actions = SystemActions();
            actions.Children.Add(ActionButton("选择项目保存盘", delegate
            {
                bool changed = false;
                if (TryAction(delegate { changed = ChooseLocation(); }) && changed) ShowSystemStorage();
            }, false));
            actions.Children.Add(ActionButton("迁移整个项目库", delegate { MigrateSystem(true, false); }, false));
            section.Children.Add(actions);

            section = SystemSection("导出副本");
            SystemDetail(section, "导出位置 · " + (Sys.ExportFolder ?? "未设置") +
                "\n设置后，可在单项制作产物旁点「导出副本」，用于交付或在其他软件中打开。");
            var exports = SystemActions();
            exports.Children.Add(ActionButton("选择导出盘", delegate
            {
                string folder = PickFolder("选择媒体导出文件夹", Sys.ExportFolder ?? preferences.Root);
                if (folder == null) return;
                if (TryAction(delegate
                {
                    var options = Sys.Copy();
                    options.ExportFolder = folder;
                    SaveSystemOptions(options);
                })) ShowSystemStorage();
            }, false));
            if (Sys.ExportFolder != null)
                exports.Children.Add(ActionButton("恢复每次选择", delegate
                {
                    if (TryAction(delegate
                    {
                        var options = Sys.Copy();
                        options.ExportFolder = null; SaveSystemOptions(options);
                    })) ShowSystemStorage();
                }, false));
            section.Children.Add(exports);

            section = SystemSection("应用资料与便携模式");
            SystemDetail(section, "当前 · " + SystemData.Active.Mode + "\n" + SystemData.Active.Folder);
            SystemDetail(section, "应用资料包括本机偏好、渠道配置、共享记忆和本机对话。便携方式可把程序与「便携资料」文件夹一起带走。");
            var location = SystemActions();
            location.Children.Add(ActionButton("打开当前资料", delegate { OpenSystemFolder(SystemData.Active.Folder); }, false));
            location.Children.Add(ActionButton("迁移应用资料", delegate { MigrateSystem(false, false); }, false));
            location.Children.Add(ActionButton("启用便携模式", delegate { MigrateSystem(false, true); }, false));
            section.Children.Add(location);
            LibraryReceipt old = null;
            if (!TryAction(delegate { old = AgentData.Read<LibraryReceipt>(ReceiptPath(SystemData.Active.Folder)); })) return;
            if (old != null && !old.Recycled)
            {
                section = SystemSection("最近一次迁移的旧库");
                SystemDetail(section, old.Source);
                var backups = SystemActions();
                backups.Children.Add(ActionButton("打开旧库", delegate { OpenSystemFolder(old.Source); }, false));
                backups.Children.Add(ActionButton("校验并移到回收站", delegate { RecycleSystemLibrary(old); }, false));
                section.Children.Add(backups);
            }
        }

        private static string ReceiptPath(string folder)
        {
            return Path.Combine(folder, "系统迁移回执.json");
        }

        private void OpenSystemFolder(string path)
        {
            TryAction(delegate
            {
                if (!Directory.Exists(path)) throw new IOException("此文件夹位置当前不可用。");
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
            });
        }

        private async void MigrateSystem(bool projects, bool portable)
        {
            if (!ReadyForSystemChange()) return;
            if (!projects && SystemData.Active.Mode == "环境变量")
            {
                SetStatus("当前由环境变量指定应用资料。请先移除此覆盖后再切换。");
                return;
            }
            string source = projects ? preferences.Root : SystemData.Active.Folder;
            if (String.IsNullOrEmpty(source) || (!Directory.Exists(source) && projects))
            {
                SetStatus("当前资料目录不可用。");
                return;
            }
            if (portable && SystemData.Active.Mode == "便携资料")
            {
                SetStatus("当前已经使用便携模式。");
                return;
            }
            string target;
            if (portable) target = SystemData.PortableFolder;
            else
            {
                string folder = PickFolder("选择新库的上一级文件夹", Path.GetDirectoryName(source));
                if (folder == null) return;
                target = Path.Combine(folder, projects ? ProjectStore.RootName : "一键成片应用资料");
            }
            string ignored;
            if (!Prompt("迁移资料", "目标位置\n" + target +
                "\n逐文件核验成功后切换。原资料作为旧库保留。" + (projects ? "" : "应用将退出，再打开时使用新位置。"),
                "开始迁移", "取消", false, out ignored)) return;
            await RunSystemWork("正在复制并核验资料…", async delegate
            {
                store.SavePreferences(preferences);
                var receipt = await Task.Run(() => SystemData.CopyLibrary(source, target));
                if (projects)
                {
                    var updated = preferences.Copy();
                    SystemData.RemapProjects(updated, source, target);
                    var remapped = projectWindows.Values.Distinct().Select(ws => new {
                        Context = ws, Folder = SystemData.Remap(ws.projectFolder, source, target)
                    }).ToList();
                    AgentData.Save(ReceiptPath(SystemData.Active.Folder), receipt);
                    store.SavePreferences(updated);
                    preferences = updated;
                    projectWindows.Clear();
                    foreach (var moved in remapped)
                    {
                        var ws = moved.Context;
                        ws.projectFolder = moved.Folder;
                        ws.preferences = updated.Copy();
                        ws.project = store.OpenProject(moved.Folder);
                        projectWindows.Add(moved.Folder, ws);
                    }
                    ShowSystemStorage();
                    SetStatus("项目库已切换到 " + target);
                }
                else
                {
                    AgentData.Save(ReceiptPath(target), receipt);
                    SystemData.ActivateCopy(target, portable);
                    ExitForSystem();
                }
            });
        }

        private async void RecycleSystemLibrary(LibraryReceipt receipt)
        {
            if (!ReadyForSystemChange()) return;
            string ignored;
            if (!Prompt("校验旧库", "将再次核验原库与新库。确认后才会把原目录移到回收站。\n" + receipt.Source,
                "核验并回收", "取消", false, out ignored)) return;
            var protectedPaths = new[] { SystemData.Active.Folder, preferences.Root, projectFolder,
                Sys.ExportFolder, SystemData.Executable };
            await RunSystemWork("正在核验旧库…", async delegate
            {
                await Task.Run(() => SystemData.RecycleOld(receipt, protectedPaths));
                AgentData.Save(ReceiptPath(SystemData.Active.Folder), receipt);
                ShowSystemStorage();
                SetStatus("旧库已移入回收站。");
            });
        }

        private bool ReadyForSystemChange()
        {
            if (!shell.apiTesting && !openWorkspaces.Any(w => w.systemBusy || w.sendingCodexTask ||
                w.refreshingAccount || w.flowCreating != null || w.assetTasks.Count != 0 || w.installCancel != null || w.checkingCodex)) return true;
            SetStatus("请等待当前检测、安装或制作任务结束后再操作。");
            return false;
        }

        private async Task RunSystemWork(string label, Func<Task> operation)
        {
            systemBusy = true;
            Window.IsEnabled = false;
            if (agentWindow != null) agentWindow.IsEnabled = false;
            SetStatus(label);
            try { await operation(); }
            catch (Exception error) { SetStatus(error.Message); }
            finally
            {
                systemBusy = false;
                if (!exiting)
                {
                    Window.IsEnabled = true;
                    if (agentWindow != null) agentWindow.IsEnabled = true;
                }
            }
        }

        private void ExitForSystem()
        {
            foreach (Workspace ws in openWorkspaces.ToArray())
            {
                ws.exiting = true;
                ws.CancelScriptReaderWork();
                if (ws.installCancel != null) ws.installCancel.Cancel();
                if (ws.agentWindow != null) ws.agentWindow.Close();
            }
            Window.Close();
        }

        private void ExportSystemMedia(string projectRoot, AgentJob job)
        {
            string target = Sys.ExportFolder;
            if (target == null)
                target = PickFolder("选择本次导出目录", preferences.Root);
            if (target == null) return;
            string saved = null;
            if (TryAction(delegate
            {
                string source = AgentData.Inside(AgentData.DirectoryFor(projectRoot), job.Path);
                saved = SystemData.ExportMedia(source, target, Path.GetFileName(projectRoot));
            })) SetStatus("已导出 " + Path.GetFileName(saved));
        }

        private void ShowSystemVersion()
        {
            BeginPage(2, "版本与诊断", Program.Version);
            AddPageBack("返回设置", ShowSettings);
            var section = SystemSection("版本与运行诊断");
            string text = "Lacuna " + Program.Version + "\nWindows · " + Environment.OSVersion.VersionString +
                "\n程序 · " + SystemData.Executable +
                "\n应用资料 · " + SystemData.Active.Mode + " · " + SystemData.Active.Folder +
                "\n项目库 · " + (preferences.Root ?? "未设置") + "\n导出 · " + (Sys.ExportFolder ?? "每次选择") +
                "\nCodex · " + (codexStatus == null ? "待检测" : codexStatus.Label + " · " + (codexStatus.Version ?? "版本未知")) +
                "\n更新方式 · 团队共享文件夹";
            var details = AgentField(section, "诊断摘要", text, 118);
            details.IsReadOnly = true;
            section.Children.Add(ActionButton("复制诊断摘要", delegate
            {
                CopyText(text, "已复制版本与路径。");
            }, false));
            section.Children.Add(ActionButton("第三方组件许可", ShowLibraryNotices, false));

            section = SystemSection("团队版更新");
            SystemDetail(section, "从团队共享文件夹下载完整 ZIP，解压到新文件夹。保存进度并退出 Lacuna，再双击新包中的「升级一键成片.exe」，确认旧版位置即可更新。");
        }

        private void ShowLibraryNotices()
        {
            BeginPage(2, "第三方组件许可", Program.Version);
            AddPageBack("返回版本与诊断", ShowSystemVersion);
            using (var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("第三方组件许可.txt")))
            {
                content.Children.Add(new TextBox
                {
                    Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true, Height = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
            }
        }

        private void ShowSystemMemory()
        {
            BeginPage(2, "共享项目记忆", null);
            AddPageBack("返回设置", ShowSettings);
            string current = CurrentAgentProject();
            var section = SystemSection("关联共享笔记");
            if (current == null)
            {
                SystemDetail(section, "先打开项目，再创建或选择要关联的笔记。");
                section.Children.Add(ActionButton("打开项目列表", ShowProjectList, false));
                return;
            }
            ProjectNoteLink link = null;
            List<SharedProjectNote> notes = null;
            if (!TryAction(delegate { link = SystemMemory.Link(current); notes = SystemMemory.List(); })) return;
            if (link != null && link.Snapshot != null && notes.All(n => n.Id != link.Snapshot.Id))
                notes.Add(link.Snapshot);
            SystemDetail(section, "当前项目 · " + Path.GetFileName(current) +
                "\n选中的笔记可以供多个项目关联，勾选后才会加入本项目的 Codex 对话。修改共享笔记也会更新其他已关联项目的引用。");
            var select = new ComboBox { DisplayMemberPath = "Title", ItemsSource = notes, MinHeight = 30,
                Margin = new Thickness(0, 10, 0, 0) };
            AutomationProperties.SetName(select, "共享笔记列表");
            section.Children.Add(select);
            var title = AgentField(section, "共享笔记标题", "");
            title.MaxLength = 100;
            var content = AgentField(section, "共享笔记正文", "", 105);
            content.MaxLength = 4000;
            var use = new CheckBox { Content = "允许 Codex 在本项目引用所选笔记", Foreground = Window.Foreground,
                IsChecked = link != null && link.Include, Margin = new Thickness(0, 12, 0, 0) };
            section.Children.Add(use);
            select.SelectionChanged += delegate
            {
                var note = select.SelectedItem as SharedProjectNote;
                title.Text = note == null ? "" : note.Title;
                content.Text = note == null ? "" : note.Text;
            };
            if (link != null && link.Snapshot != null)
                select.SelectedItem = notes.FirstOrDefault(n => n.Id == link.Snapshot.Id);
            var actions = SystemActions();
            actions.Children.Add(ActionButton("新建笔记", delegate
            {
                select.SelectedItem = null;
                title.Text = content.Text = "";
                use.IsChecked = false;
                title.Focus();
            }, false));
            actions.Children.Add(ActionButton("保存并关联", delegate
            {
                var note = select.SelectedItem as SharedProjectNote;
                if (TryAction(delegate { SystemMemory.Save(current, note == null ? null : note.Id, title.Text, content.Text, use.IsChecked == true); }))
                {
                    ShowSystemMemory();
                    SetStatus("已保存当前项目的记忆关联。");
                }
            }, true));
            actions.Children.Add(ActionButton("取消关联", delegate
            {
                if (TryAction(delegate { SystemMemory.SetLink(current, null, false); }))
                {
                    ShowSystemMemory();
                    SetStatus("已取消当前项目的引用。");
                }
            }, false));
            section.Children.Add(actions);
        }

    }
}
