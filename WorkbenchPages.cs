using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private const int RecentLimit = 5;
        private DrawingBrush bannerArtwork;

        private sealed class ProjectEntry
        {
            internal string Folder;
            internal string Name;
            internal string Error;
            internal DateTime Updated;
            internal ProjectProgress Progress;
        }

        private string DisplayName()
        {
            return project == null ? null : project.Name;
        }

        private IEnumerable<RecentProject> RecentProjects()
        {
            return (preferences.RecentProjects ?? Enumerable.Empty<RecentProject>())
                .Where(item => item != null && !String.IsNullOrEmpty(item.Folder) && store.HasProgress(item.Folder));
        }

        private static bool SameFolder(string first, string second)
        {
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private void ShowWorkbench()
        {
            BeginPage(0, null, null);

            var entries = RecentProjects()
                .Take(RecentLimit).Select(item => Describe(item.Folder, item.Name)).ToList();
            DrawStudioHome(entries);

        }

        private void ShowProjectList()
        {
            BeginPage(1, "项目", null);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            actions.Children.Add(ActionButton("一键成片", OpenFlowWizard, true));
            actions.Children.Add(ActionButton("打开项目", OpenProject, false));
            content.Children.Add(actions);

            string root = preferences.Root;
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                content.Children.Add(Notice("请选择可用的保存位置，查看这里的项目。"));
                AddPageBack("设置保存位置", ShowSettings);
                return;
            }

            List<ProjectEntry> entries = null;
            if (!TryAction(delegate
            {
                entries = Directory.EnumerateDirectories(root).Where(folder => !Path.GetFileName(folder).StartsWith(".new-", StringComparison.Ordinal) && store.HasProgress(folder))
                    .Select(folder => Describe(folder, null))
                    .OrderByDescending(entry => entry.Updated).ThenBy(entry => entry.Name).ToList();
            }))
            {
                content.Children.Add(Notice("无法读取项目列表。请核对保存位置。"));
                AddPageBack("设置保存位置", ShowSettings);
                return;
            }

            content.Children.Add(Muted("按项目名查找", 13, 0));
            var search = new TextBox
            {
                MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 245,
                MinHeight = 38, Margin = new Thickness(0, 5, 0, 13)
            };
            AutomationProperties.SetName(search, "按项目名查找");
            content.Children.Add(search);
            var rows = new StackPanel();
            content.Children.Add(rows);
            Action refresh = delegate
            {
                rows.Children.Clear();
                string filter = search.Text.Trim();
                foreach (ProjectEntry entry in entries)
                    if (entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        rows.Children.Add(ProjectRow(entry, false));
                if (rows.Children.Count == 0)
                    rows.Children.Add(Notice(filter.Length == 0 ? "当前位置暂无项目。" : "没有找到匹配的项目。"));
            };
            search.TextChanged += delegate { refresh(); };
            refresh();
        }

        private void ShowSettings()
        {
            BeginPage(2, "设置", null);
            if (CodexAuthMode.PreferenceCorrupt) content.Children.Add(Notice(CodexAuthMode.PreferenceWarning));
            var settings = new StackPanel { Margin = new Thickness(16) };
            settings.Children.Add(Title("项目保存位置", 19));
            var path = Muted(preferences.Root ?? "尚未设置保存位置", 14, 8);
            path.TextWrapping = TextWrapping.Wrap;
            settings.Children.Add(path);
            var choose = ActionButton("选择保存位置", delegate
            {
                bool changed = false;
                if (TryAction(delegate { changed = ChooseLocation(); }) && changed)
                    ShowSettings();
            }, true);
            choose.Margin = new Thickness(0, 14, 0, 0);
            choose.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Children.Add(choose);
            content.Children.Add(Panel(settings));
            AddSystemSettings();
            AddCodexSettings();
            AddCodexConnectionSettings();
            AddImageApiSettings();
        }

        private void AddCodexConnectionSettings()
        {
            var section = SystemSection("Codex 连接方式");
            var account=new RadioButton {Content="Codex 账号登录（默认）",Foreground=Window.Foreground,IsChecked=!CodexAuthMode.IsApi};
            var api=new RadioButton {Content="OpenAI Responses API 直连",Foreground=Window.Foreground,IsChecked=CodexAuthMode.IsApi};
            section.Children.Add(account);
            section.Children.Add(api);
            SystemDetail(section, "全局使用 Codex 账号登录时，也可在此保存 Base URL、API 模型 ID 与项目 Key，供创建项目或资产页单独选用「OpenAI Responses API 生图」。");
            section.Children.Add(Muted("Base URL",13,8));
            var endpoint=CodexTextBox("Base URL",CodexAuthMode.BaseUrl,32);
            section.Children.Add(endpoint);
            var official = ActionButton("使用 OpenAI 官方地址", () => endpoint.Text = CodexAuthMode.DefaultUrl, false);
            official.HorizontalAlignment = HorizontalAlignment.Left;
            section.Children.Add(official);
            section.Children.Add(Muted("API 模型 ID（可手填）",13,8));
            var model=CodexTextBox("API 模型",CodexAuthMode.Model,32);
            section.Children.Add(model);
            var modelsLabel = Muted("从 API 模型列表选择", 13, 8);
            modelsLabel.Visibility = Visibility.Collapsed;
            section.Children.Add(modelsLabel);
            var models = new ComboBox { MinHeight = 32, MaxDropDownHeight = 290, Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(models, "API 模型列表");
            section.Children.Add(models);
            string source=null;
            CodexApiModels loaded = null;
            Action resetModels = delegate
            {
                loaded = null;
                models.ItemsSource = null;
                modelsLabel.Visibility = models.Visibility = Visibility.Collapsed;
            };

            SystemDetail(section, "测试连接后可选模型。读取列表需要 List models / Read 权限。应用直连设置前请填写或选择模型 ID。");
            var status = Muted("", 13, 8);
            status.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(status);

            models.SelectionChanged += delegate
            {
                var selected = models.SelectedItem as string;
                if (selected == null || loaded == null) return;
                model.Text = selected;
                status.Text = source+" · "+loaded.Describe(selected);
            };

            var label = Muted("OpenAI 项目 Key", 13, 10);
            section.Children.Add(label);
            var input = new PasswordBox
            {
                MaxLength = 1024,
                MinHeight = 36,
                MaxWidth = 500,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brush("Input"),
                Foreground = Window.Foreground,
                BorderBrush = Brush("Line"),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 4, 0, 0)
            };
            AutomationProperties.SetName(input, "OpenAI 项目 Key");
            var plain = CodexTextBox("当前输入的明文 Key","",36);
            plain.MaxLength = input.MaxLength;
            plain.Visibility = Visibility.Collapsed;
            var row = new DockPanel();
            var toggle = new Button { Content="显示 Key", MinWidth=78, Margin=new Thickness(6,0,0,0) };
            AutomationProperties.SetName(toggle,"显示 Key");
            DockPanel.SetDock(toggle,Dock.Right); row.Children.Add(toggle);
            var editor = new Grid(); editor.Children.Add(input); editor.Children.Add(plain); row.Children.Add(editor);
            section.Children.Add(row);
            bool syncing=false;
            int caret=-1,selection=0;
            Action hide = delegate {
                syncing=true;
                if(plain.Visibility==Visibility.Visible){caret=plain.SelectionStart;selection=plain.SelectionLength;}
                plain.Visibility=Visibility.Collapsed; plain.Clear();
                input.Visibility=Visibility.Visible; toggle.Content="显示 Key";
                AutomationProperties.SetName(toggle,"显示 Key"); syncing=false;
            };
            toggle.Click += delegate {
                if(plain.Visibility==Visibility.Visible) { hide(); input.Focus(); }
                else {
                    syncing=true; plain.Text=input.Password; syncing=false;
                    input.Visibility=Visibility.Collapsed; plain.Visibility=Visibility.Visible;
                    toggle.Content="隐藏 Key"; AutomationProperties.SetName(toggle,"隐藏 Key");
                    plain.Focus(); int start=caret<0?plain.Text.Length:Math.Min(caret,plain.Text.Length);
                    plain.Select(start,Math.Min(selection,plain.Text.Length-start));
                }
            };
            EventHandler deactivate=delegate { hide(); };
            Window.Deactivated+=deactivate;
            Action cleanup=null;
            cleanup=delegate {
                Window.Deactivated-=deactivate; hide(); input.Clear();
                if(closeKeyEditor==cleanup) closeKeyEditor=null;
            };
            closeKeyEditor=cleanup;
            row.Unloaded+=delegate { cleanup(); };
            plain.TextChanged+=delegate { if(!syncing) input.Password=plain.Text; };

            var actions = SystemActions();
            section.Children.Add(actions);
            Button save = null;
            Button clear = null;
            Action refresh = delegate
            {
                bool saved = AnalysisKeyStore.HasSavedKey;
                bool hasDraft;
                using (var password = input.SecurePassword) hasDraft = password.Length > 0;
                status.Text = (hasDraft?"使用当前输入（未保存） · ":CodexAuthMode.HasLaunchKey?"使用本次启动 Key · ":saved?"使用已保存的 Key · ":"")+CodexAuthMode.Label+" · "+(saved ? "已用当前 Windows 用户加密保存" : "尚未保存项目 Key") +
                    (CodexAuthMode.HasLaunchKey ? " · 本次启动使用临时 Key" : "");
                clear.IsEnabled = saved;
                save.IsEnabled = hasDraft;
            };
            save = ActionButton("保存或替换 Key", delegate
            {
                hide();
                if (ReadyForSystemChange() && TryAction(() => AnalysisKeyStore.Save(input.Password)))
                {
                    CodexAuthMode.ClearActiveKey();
                    CodexAppServer.ResetConnection();
                    hide(); input.Clear();
                    resetModels();
                    refresh();
                    SetStatus("已在本机加密保存 OpenAI 项目 Key。");
                }
            }, true);
            clear = ActionButton("清除已保存的 Key", delegate
            {
                hide();
                if (!ReadyForSystemChange())return;
                if (CodexAuthMode.IsApi){SetStatus("请先切回账号登录，再清除 Key。");return;}
                if (TryAction(AnalysisKeyStore.Clear))
                {
                    CodexAuthMode.ClearActiveKey();
                    CodexAppServer.ResetConnection();
                    hide(); input.Clear();
                    resetModels();
                    refresh();
                    SetStatus("已清除本机保存的 OpenAI 项目 Key。");
                }
            }, false);
            actions.Children.Add(ActionButton("应用连接设置",delegate
            {
                if(!ReadyForSystemChange())return;
                bool next=api.IsChecked==true;
                if(next && input.Password.Length>0){status.Text="请先点「保存或替换 Key」再应用连接设置。";SetStatus(status.Text);return;}
                string address = endpoint.Text;
                if(next && !CodexAuthMode.HasKey){SetStatus("请先保存项目 Key。");return;}
                if (next && !TryAction(() => address = CodexAuthMode.Preflight(address, model.Text))) return;
                if(next &&
                    MessageBox.Show(Window,"剧本分析、资产提示词与审核、视觉自检、内置出图、分镜、分段、视频提示词及助手将直接调用 OpenAI Responses API，不经过 Codex CLI。费用计入该 Key 所属组织。\n发送目标："+address,
                        "确认 Codex 连接方式",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
                if(TryAction(()=>CodexAuthMode.Save(next,address,model.Text)))
                {
                    foreach(var workspace in openWorkspaces){workspace.codexStatus=null;workspace.RefreshCodex();}
                    ShowSettings();
                    SetStatus("已应用 "+CodexAuthMode.Label);
                }
            },true));
            actions.Children.Add(ActionButton("测试 API 连接",delegate
            {
                if(shell.apiTesting || systemBusy)return;
                resetModels();
                string key=input.Password;
                try {
                    var endpointUri=new Uri(CodexAuthMode.ValidateUrl(endpoint.Text)+"/models");
                    key=CodexAuthMode.TestKey(key,out source);
                    hide();
                    string outcome;
                    loaded=TestApiConnection(endpointUri,key,out outcome);
                    if(loaded!=null){
                        models.ItemsSource=loaded.Ids;
                        models.SelectedItem=model.Text.Trim();
                        modelsLabel.Visibility=models.Visibility=loaded.Ids.Length>0?Visibility.Visible:Visibility.Collapsed;
                        outcome=loaded.Describe(model.Text.Trim());
                    }
                    status.Text=source+" · "+CodexAuthMode.RedactTestMessage(outcome,key);
                } catch(Exception e){status.Text=CodexAuthMode.RedactTestMessage(e.GetBaseException().Message,key);}
                SetStatus(status.Text);
            },false));
            actions.Children.Add(save);
            actions.Children.Add(clear);
            input.PasswordChanged += delegate { resetModels(); refresh(); };
            endpoint.TextChanged += delegate { resetModels(); refresh(); };
            refresh();
        }

        private ProjectEntry Describe(string folder, string fallbackName)
        {
            var entry = new ProjectEntry { Folder = folder, Name = fallbackName ?? Path.GetFileName(folder) };
            if (!Directory.Exists(folder))
            {
                entry.Error = "找不到项目文件夹";
                return entry;
            }
            try
            {
                entry.Updated = store.UpdatedAt(folder);
                entry.Progress = store.OpenProject(folder);
                entry.Name = entry.Progress.Name;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is InvalidOperationException || error is ArgumentException))
                    throw;
                entry.Error = "无法读取项目 · " + error.Message;
            }
            return entry;
        }

        private FrameworkElement ProjectRow(ProjectEntry entry, bool recent)
        {
            var row = new DockPanel { LastChildFill = true };
            bool available = entry.Progress != null;
            string label = available ? ProjectActionLabel(entry.Progress, entry.Folder) : "重新定位";
            var shortcut = ActionButton(label, delegate
            {
                if (available)
                    EnterProject(entry, true);
                else
                    Relocate(entry);
            }, false);
            shortcut.VerticalAlignment = VerticalAlignment.Center;
            shortcut.Margin = new Thickness(10, 0, 0, 0);
            AddRight(row, shortcut);
            if (!recent && available)
                AddRight(row, ActionButton("删除项目", () => DeleteListedProject(entry), false));
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Title(entry.Name, 16);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            nameRow.Children.Add(name);
            if (available)
            {
                var tag = StatusBadge(entry.Progress.CategoryLabel, entry.Progress.Automatic ? "Accent" : "Default");
                tag.Margin = new Thickness(8, 0, 0, 0);
                nameRow.Children.Add(tag);
            }
            details.Children.Add(nameRow);
            string sub = available
                ? (entry.Progress.Automatic ? FlowCreation.Status(entry.Folder) : ScriptState(entry.Progress, entry.Folder))
                : entry.Error;
            var caption = Muted(sub, 12.5, 4);
            if (!available) caption.Foreground = Brush("Review");
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(caption);

            var openDetails = ActionButton(details, delegate
            {
                if (available) EnterProject(entry, false);
                else Relocate(entry);
            }, false);
            openDetails.Style = Style("GhostButton");
            openDetails.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            openDetails.Padding = new Thickness(6, 4, 8, 4);
            openDetails.Margin = new Thickness(0);
            AutomationProperties.SetName(openDetails, entry.Name);
            openDetails.ToolTip = entry.Error ?? entry.Folder;
            row.Children.Add(openDetails);
            row.Margin = new Thickness(10, 8, 12, 8);
            var panel = Panel(row);
            panel.Margin = new Thickness(0, 0, 0, 9);
            if (available) panel.MouseLeftButtonUp += delegate { EnterProject(entry, false); };
            return panel;
        }

        private void DeleteListedProject(ProjectEntry entry)
        {
            Workspace owner;
            projectWindows.TryGetValue(Path.GetFullPath(entry.Folder), out owner);
            if (openWorkspaces.Any(w => w.systemBusy || w.flowCreating != null ||
                w.assetTasks.Any(t => SameProject(t.Root, entry.Folder))) ||
                (owner != null && (owner.sendingCodexTask || owner.scriptImportCancel != null)))
            {
                SetStatus("项目有操作正在进行，请等待完成后再删除。");
                return;
            }
            string ignored;
            if (!Prompt("删除项目", "将「" + entry.Name + "」及其制作资料移入项目库的「.lacuna-trash」文件夹。可将其中的项目文件夹移回原位恢复。\\n" +
                entry.Folder, "删除项目", "取消", false, out ignored)) return;
            TryAction(delegate {
                LocalPreferences updated = store.LoadPreferences();
                string archived = store.TrashProject(updated, entry.Folder);
                foreach (Workspace ws in openWorkspaces) ws.preferences = updated.Copy();
                if (owner != null)
                {
                    owner.CancelScriptReaderWork();
                    if (owner.agentWindow != null)
                    {
                        Window detached = owner.agentWindow;
                        owner.agentWindow = null;
                        detached.Close();
                    }
                    owner.projectFolder = null;
                    owner.project = null;
                    owner.agentPageProject = null;
                    owner.agentDestination = null;
                    projectWindows.Remove(Path.GetFullPath(entry.Folder));
                    if (owner != this && owner != shell) openWorkspaces.Remove(owner);
                    if (owner == this) Window.Title = "Lacuna · " + Program.Version;
                }
                foreach (var notice in shell.notices.Where(n => SameProject(n.Folder, entry.Folder)).ToArray()) notice.Dismiss();
                ShowProjectList();
                SetStatus("项目已移至 " + archived);
            });
        }

        private void EnterProject(ProjectEntry entry, bool shortcut)
        {
            TryAction(delegate {
                var target = ProjectWindow(entry.Folder);
                if (shortcut) target.OpenProjectAction(); else target.ShowOverview();
            });
        }

        private void Relocate(ProjectEntry entry)
        {
            string selected = PickFolder("重新选择「" + entry.Name + "」的项目文件夹", preferences.Root);
            if (selected != null) TryAction(() => ProjectWindow(selected, entry.Folder).ShowOverview());
        }

        private string ProjectActionLabel(ProjectProgress progress, string folder)
        {
            if (progress.Automatic) return "查看制作进度";
            if (String.IsNullOrEmpty(progress.Script))
                return "导入剧本";
            string state = AnalysisState(folder, progress);
            if (state == "分析待审核") return "去审核";
            if (state == "资产清单已交接") return "继续资产制作";
            if (state == "分析中") return "查看分析进度";
            return File.Exists(store.ProjectFile(folder, progress.Script)) ? "去分析" : "重新导入剧本";
        }

        private string ScriptState(ProjectProgress progress, string folder)
        {
            return AnalysisState(folder, progress);
        }

        private void OpenProjectAction() { ShowOverview(); }

        private void ShowOverview()
        {
            if (project != null && project.Automatic) ShowFlowProject();
            else ShowProjectList();
        }

        private Border Cover(UIElement child, bool useArtwork, double height)
        {
            var cover = Panel(child);
            cover.MinHeight = height;
            cover.ClipToBounds = true;
            if (!useArtwork)
                return cover;

            if (bannerArtwork == null)
                using (var stream = typeof(Workspace).Assembly.GetManifestResourceStream("Workbench.png"))
                {
                    var artwork = new BitmapImage();
                    artwork.BeginInit();
                    artwork.CacheOption = BitmapCacheOption.OnLoad;
                    artwork.StreamSource = stream;
                    artwork.EndInit();
                    artwork.Freeze();
                    var horizontal = new DrawingGroup { OpacityMask = BannerEdgeFade(new Point(1, 0)) };
                    horizontal.Children.Add(new ImageDrawing(artwork,
                        new Rect(0, 0, artwork.PixelWidth, artwork.PixelHeight)));
                    var vertical = new DrawingGroup { OpacityMask = BannerEdgeFade(new Point(0, 1)) };
                    vertical.Children.Add(horizontal);
                    bannerArtwork = new DrawingBrush(vertical)
                    {
                        Stretch = Stretch.Uniform, AlignmentX = AlignmentX.Right, AlignmentY = AlignmentY.Center
                    };
                    bannerArtwork.Freeze();
                }
            cover.Background = bannerArtwork;
            return cover;
        }

        private static LinearGradientBrush BannerEdgeFade(Point direction)
        {
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = direction };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.96));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            return mask;
        }

        private TextBlock Title(string text, double size)
        {
            var title = Text(text, size, 0);
            title.FontWeight = FontWeights.SemiBold;
            return title;
        }

        private Border Notice(string text)
        {
            var row = new DockPanel();
            var icon = Icon("\uE946", 15);
            icon.Foreground = Brush("AccentText");
            icon.Margin = new Thickness(0, 1, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            AddLeft(row, icon);
            var note = Muted(text, 13.5, 0);
            note.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(note);
            var panel = new Border
            {
                Background = Brush("PanelLift"),
                BorderBrush = Brush("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 4, 0, 10),
                Child = row
            };
            return panel;
        }

        private void AddPageBack(string label, Action action)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var chevron = Text("‹", 17, 0);
            chevron.Margin = new Thickness(0, -2, 6, 0);
            row.Children.Add(chevron);
            row.Children.Add(Text(SystemWord(label), 13.5, 0));
            var back = ActionButton(row, action, false);
            back.Style = Style("GhostButton");
            back.HorizontalAlignment = HorizontalAlignment.Left;
            back.Margin = new Thickness(-8, 0, 0, 14);
            AutomationProperties.SetName(back, label);
            content.Children.Add(back);
        }
    }
}




