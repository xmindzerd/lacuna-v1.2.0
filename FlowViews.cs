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
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private const string FlowTaskName = "一键成片";
        private FlowDraft flowDraft;
        private string WindowDraft { get { return projectFolder == null ? FlowCreation.DraftFile :
            Path.Combine(AgentData.Local,"创建草稿",AgentData.Hash(projectFolder.ToUpperInvariant())+".json"); } }
        private CancellationTokenSource flowCreating;
        private TextBlock flowCreatingLabel;
        private int flowCreatingPage = -1;
        private FrameworkElement flowProjectMarker;

        private void OpenFlowWizard()
        {
            if (flowCreating != null) { ShowCreating(); return; }
            if ((String.IsNullOrEmpty(preferences.Root) || !Directory.Exists(preferences.Root)) && !ChooseLocation()) return;
            TryAction(delegate
            {
                if (flowDraft == null) flowDraft = FlowCreation.NewDraft(WindowDraft);
                ShowFlowWizard();
            });
        }

        private static void NameControl(FrameworkElement control, string name) { AutomationProperties.SetName(control, name); }

        private async void ShowFlowWizard()
        {
            var draft = flowDraft;
            BeginPage(0, "新建一键成片项目", "导入剧本，配置制作方式。项目创建成功后即可开始制作。");
            AddPageBack("返回工作台", delegate { AgentData.Save(WindowDraft, draft); ShowWorkbench(); });
            int generation = pageRevision;
            var setup = new StackPanel { Margin = new Thickness(16) };
            setup.Children.Add(Title("项目信息", 19));
            var name = AgentField(setup, "项目名称", draft.Name ?? "");
            NameControl(name, "项目名称");
            name.TextChanged += delegate { draft.Name = name.Text; };
            var script = AgentField(setup, "剧本文件", draft.Script ?? "");
            NameControl(script, "剧本文件");
            script.TextChanged += delegate { draft.Script = script.Text.Trim().Trim('"'); };
            setup.Children.Add(ActionButton("选择剧本", delegate {
                var picker = new OpenFileDialog { Filter = "剧本|*.txt;*.md;*.docx;*.pdf", CheckFileExists = true };
                if (picker.ShowDialog(Window) == true)
                {
                    script.Text = picker.FileName;
                    if (String.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(picker.FileName);
                }
            }, false));
            setup.Children.Add(Muted("成片画幅", 14, 12));
            draft.Ratio = "9:16";
            var ratio = StudioText("竖屏 9:16（一键成片固定竖屏，资产图默认横屏 4:3）",14,4);
            AutomationProperties.SetName(ratio, "项目画幅");
            setup.Children.Add(ratio);
            setup.Children.Add(DialogueLanguagePicker(draft.Language, value => draft.Language = value));
            var review = new CheckBox { Content = "资产生成完后暂停，等我人工查看确认（支持一键确认继续）", IsChecked = draft.Manual, Margin = new Thickness(0,5,0,6), Foreground = Window.Foreground };
            review.Checked += delegate { draft.Manual = true; }; review.Unchecked += delegate { draft.Manual = false; };
            setup.Children.Add(review);
            var skip = Muted("跳过资产确认，自动一直运行到最终提示词后让我确认",13,4);
            skip.Visibility=draft.Manual ? Visibility.Collapsed : Visibility.Visible;
            review.Checked += delegate { skip.Visibility=Visibility.Collapsed; };
            review.Unchecked += delegate { skip.Visibility=Visibility.Visible; };
            setup.Children.Add(skip);
            AddVideoSettings(setup,draft);
            setup.Children.Add(Muted("资产出图渠道", 14, 13));
            var channels = new ComboBox { MinHeight = 35, Margin = new Thickness(0,4,0,5), IsEnabled = false };
            NameControl(channels, "资产出图渠道");
            var builtin = new ComboBoxItem { Content = "Codex 内置生图", Tag = "builtin" };
            channels.Items.Add(builtin);
            channels.Items.Add(new ComboBoxItem { Content = FlowChannels.ResponsesApiName, Tag = FlowChannels.ResponsesApi });
            var channelNote = StudioText("正在检测本机可用的图片 CLI…", 13, 2);
            setup.Children.Add(channels); setup.Children.Add(channelNote);

            var skills = new StackPanel { Margin = new Thickness(16) };
            skills.Children.Add(Title("最终提示词 Skill", 19));
            var standard = new RadioButton { Content="使用内置提示词 Skill · xiaoyu-seedance-one-click-prompts（晓羽 Seedance 一键成片提示词）",
                IsChecked=draft.FinalPromptSkillMode != "custom", Foreground=Window.Foreground, Margin=new Thickness(0,10,0,6) };
            var custom = new RadioButton { Content="使用我的自定义提示词 Skill", IsChecked=draft.FinalPromptSkillMode == "custom",
                Foreground=Window.Foreground, Margin=new Thickness(0,4,0,6) };
            NameControl(standard,"内置最终提示词 Skill");
            NameControl(custom,"自定义最终提示词 Skill");
            skills.Children.Add(standard);
            skills.Children.Add(custom);
            var customPanel = new StackPanel();
            skills.Children.Add(customPanel);
            Action mode = delegate {
                draft.FinalPromptSkillMode = custom.IsChecked == true ? "custom" : "builtin";
                customPanel.Visibility = custom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            };
            standard.Checked += delegate { mode(); };
            custom.Checked += delegate { mode(); };
            var search = AgentField(customPanel, "查找本机 Skill", "");
            var list = new ListBox { MaxHeight=180, SelectionMode=SelectionMode.Single,
                Background=Brush("Input"), Foreground=Window.Foreground };
            NameControl(list,"本机最终提示词 Skill 列表");
            customPanel.Children.Add(list);
            var selected = StudioText("", 13, 4);
            customPanel.Children.Add(selected);
            var paths = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            Action draw = delegate {
                list.Items.Clear();
                foreach (var p in paths.OrderBy(p=>p.Value))
                {
                    if (p.Value.IndexOf(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)<0) continue;
                    var item = new ListBoxItem { Content=p.Value, Tag=p.Key, ToolTip=p.Key };
                    list.Items.Add(item);
                    if (String.Equals(p.Key,draft.FinalPromptSkillFile,StringComparison.OrdinalIgnoreCase)) list.SelectedItem=item;
                }
                selected.Text=String.IsNullOrEmpty(draft.FinalPromptSkillFile) ? "请选择一个 SKILL.md" : draft.FinalPromptSkillFile;
            };
            Action catalog = delegate {
                if (paths.Count == 0)
                    foreach (var local in LocalSkills.List(null)) paths[Path.Combine(local.Directory,"SKILL.md")]=local.Name;
                if (!String.IsNullOrEmpty(draft.FinalPromptSkillFile) && !paths.ContainsKey(draft.FinalPromptSkillFile))
                    paths[draft.FinalPromptSkillFile]=Path.GetFileName(Path.GetDirectoryName(draft.FinalPromptSkillFile));
                draw();
            };
            list.SelectionChanged += delegate {
                var item=list.SelectedItem as ListBoxItem;
                if (item != null) selected.Text=draft.FinalPromptSkillFile=(string)item.Tag;
            };
            search.TextChanged += delegate { draw(); };
            custom.Checked += delegate { TryAction(catalog); };
            mode();
            if (custom.IsChecked == true) catalog();
            customPanel.Children.Add(ActionButton("从文件选择 SKILL.md", delegate {
                var picker=new OpenFileDialog {Filter="Skill 定义|SKILL.md",CheckFileExists=true};
                if (picker.ShowDialog(Window)==true) TryAction(delegate {
                    var package=FlowSkills.Inspect(picker.FileName);
                    draft.FinalPromptSkillFile=picker.FileName;
                    paths[picker.FileName]=package.Skill.Name;
                    search.Text=""; draw();
                });
            },false));
            var defaults=new CheckBox { Content="设为默认自定义提示词 Skill", IsChecked=draft.DefaultCustomPromptSkill,
                Foreground=Window.Foreground, Margin=new Thickness(0,8,0,4) };
            defaults.Checked += delegate { draft.DefaultCustomPromptSkill=true; };
            defaults.Unchecked += delegate { draft.DefaultCustomPromptSkill=false; };
            customPanel.Children.Add(defaults);
            var upstream = new StackPanel();
            foreach (var line in new[] {
                "1A 导演剧本分析 · xiaoyu-director-script-analysis",
                "1B 视觉设定与资产拆解 · short-drama-assets-zenstory（同套服装多状态二级派生锁定）",
                "2A 资产出图与参考规范 · lacuna-asset-image-rules",
                "03 竖屏导演分镜 · xiaoyu-vertical-shotlist" })
                upstream.Children.Add(StudioText(line,13,5));
            skills.Children.Add(new Expander {Header="查看内置上游工作流 Skill（自动串联 · 共 4 项）", Content=upstream,
                Foreground=Window.Foreground, Margin=new Thickness(0,12,0,4)});
            content.Children.Add(StudioColumns(Panel(setup), Panel(skills), 1, 1, 720));
            var available = new List<AssetChannel>();
            flowAccount=Muted(CodexAppServer.Summary(agentOptions),12,8); setup.Children.Add(flowAccount);
            var create = ActionButton("创建一键成片项目", () =>
            {
                if (draft.FinalPromptSkillMode=="custom" && !File.Exists(draft.FinalPromptSkillFile))
                { SetStatus("请选择自定义最终提示词 Skill 的 SKILL.md 文件。"); return; }
                if (channels.SelectedItem == null) { SetStatus("请选择可用的出图渠道。"); return; }
                if (draft.Video != null && draft.Video.Enabled && String.IsNullOrWhiteSpace(draft.Video.Model))
                { SetStatus("正在读取即梦 CLI 可用模型，请稍候完成后再创建项目。"); return; }
                draft.Channel = (string)((ComboBoxItem)channels.SelectedItem).Tag;
                CreateFlow(FlowChannels.IsResponsesApi(draft.Channel) ? FlowChannels.ResponsesApiChannel() : available.FirstOrDefault(c => c.Id == draft.Channel));
            }, true);
            create.Margin = new Thickness(0,12,0,4); create.HorizontalAlignment = HorizontalAlignment.Left; create.IsEnabled = false;
            content.Children.Add(create);
            string cliSummary = "";
            channels.SelectionChanged += delegate
            {
                var choice = channels.SelectedItem as ComboBoxItem;
                if (choice == null) return;
                draft.Channel = (string)choice.Tag;
                if (FlowChannels.IsResponsesApi(draft.Channel))
                {
                    string reason;
                    channelNote.Text = CodexAuthMode.ResponsesApiReady(out reason)
                        ? reason + " · 仅资产出图走 API，其余步骤沿用全局连接。" : reason;
                }
                else channelNote.Text = cliSummary;
            };
            try
            {
                available = await Task.Run(() => FlowChannels.Available(CancellationToken.None));
                if (generation != pageRevision) return;
                foreach (var c in available) channels.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
                cliSummary = available.Count == 0 ? "本机未检测到可用的外部图片 CLI。" : "已通过健康检查的外部渠道 · " + available.Count + " 个";
            }
            catch (Exception e)
            {
                if (generation != pageRevision) return;
                cliSummary = "外部渠道检测未完成 · " + AgentData.Short(e.Message,120);
            }
            channels.SelectedItem = channels.Items.Cast<ComboBoxItem>().FirstOrDefault(p => (string)p.Tag == draft.Channel);
            if (channels.SelectedItem == null) channelNote.Text = "上次选择的 CLI 当前不可用，请重新选择。";
            channels.IsEnabled = true; create.IsEnabled = true;
        }

        private void ShowCreating()
        {
            BeginPage(0, "项目正在创建中", flowDraft.Name);
            flowCreatingPage = pageRevision;
            var card = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            flowCreatingLabel = ProjectAnalysisParagraph("正在准备剧本和制作方案…", 15);
            card.Children.Add(flowCreatingLabel);
            card.Children.Add(new ProgressBar { Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 18) });
            var backBtn = ActionButton("返回修改", () => UiFx.SafeCancel(flowCreating), false);
            backBtn.HorizontalAlignment = HorizontalAlignment.Left;
            card.Children.Add(backBtn);
            content.Children.Add(Panel(card));
        }

        private bool IsShowingCreating()
        {
            return IsCurrentView && flowCreatingPage == pageRevision && flowCreatingLabel != null;
        }

        private async void CreateFlow(AssetChannel channel)
        {
            var draft = flowDraft;
            if (flowCreating != null) return;
            string parent = preferences.Root;
            if (!TryAction(delegate { ProjectStore.ValidateName((draft.Name ?? "").Trim()); if (!File.Exists(draft.Script)) throw new FileNotFoundException("请选择剧本文件。"); AgentData.Save(WindowDraft,draft); })) return;
            var task = new AssetTask { Name="创建一键成片项目", Root=Path.Combine(parent,draft.Name.Trim()), Status="准备制作方案" };
            string folder = null, error = null, draftFile=WindowDraft;
            try
            {
                BeginAssetTask(task);
                flowCreating = task.Cancel;
                ShowCreating();
                folder = await Task.Run(() => FlowCreation.Create(store,parent,draft,null,null,channel,null,null,
                    msg => Window.Dispatcher.BeginInvoke(new Action(() => { if(assetTasks.Contains(task)){task.Status=msg;if(IsShowingCreating())flowCreatingLabel.Text=task.TimedStatus;} })),task.Cancel.Token,draftFile));
            }
            catch (OperationCanceledException) { error="创建表单已保留。"; }
            catch (Exception e) { error=AgentData.Short(e.GetBaseException().Message,750); }
            finally { EndAssetTask(task); task.Cancel.Dispose(); flowCreating=null; }
            bool onForm=CanShowProjectDialog && IsShowingCreating();
            flowCreatingPage=-1;
            if (folder != null)
            {
                flowDraft=null;
                TryAction(delegate { if (File.Exists(draftFile)) File.Delete(draftFile); });
                if (onForm) TryAction(delegate {
                    var created = ProjectWindow(folder);
                    created.ShowFlowProject();
                    created.SetStatus("一键成片项目已创建 · " + draft.Name);
                });
                else NotifyCreated(folder, draft.Name);
            }
            else { if (onForm) ShowFlowWizard(); if (error != null) SetStatus(error); }
            if (exitAfterCodexTask) FinishExit();
        }

        private void PreviewAnalysisMarkdown(string path)
        {
            var dialog = StyleDialog(new Window { Title = Path.GetFileNameWithoutExtension(path), Owner = Window,
                Width = 800, Height = 660, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("Panel") });
            var panel = new DockPanel { Margin = new Thickness(16) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            foreach (var target in new[] { path, Path.GetDirectoryName(path) })
            {
                string file = target;
                buttons.Children.Add(ActionButton(file == path ? "用系统程序打开" : "打开所在文件夹", () =>
                    TryAction(() => { using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })) { } }), false));
            }
            var close = ActionButton("关闭", () => dialog.Close(), false);
            close.IsCancel = true;
            buttons.Children.Add(close);
            panel.Children.Add(new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = File.ReadAllText(path) });
            dialog.Content = panel;
            dialog.ShowDialog();
        }

        private void ShowFlowProject()
        {
            string root = projectFolder;
            if (root == null) return;
            LoadStageProgress();
            BeginPage(1, project.Name, "一键成片项目总览", 0);
            AddPageBack("返回项目列表", ShowProjectList);
            TryAction(delegate
            {
                var cfg=FlowCreation.Config(root);
                if (cfg==null) throw new InvalidDataException("一键成片项目缺少配置文件（项目配置.json）。");
                if (cfg.Video==null) cfg.Video=new VideoOptions();
                var s=FlowCreation.State(root);
                FlowSkillPlan plan=null;
                try { plan=AgentData.Read<FlowSkillPlan>(FlowSkills.PlanPath(root)); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                RenderFlowOverview(root, cfg, s, plan);
            });
        }

        private static AssetWork FlowWork(string root)
        {
            var version=AnalysisData.Load(root).Confirmed(AnalysisData.CurrentScript(root));
            if (version==null) throw new InvalidOperationException("等待剧本分析确认。");
            return AssetProduction.Load(root,version,AnalysisData.ReadResult(root,version));
        }

        private async void StartFlow(string root, string approval, string retryAttempt = null)
        {
            if (AssetRunning(root,FlowTaskName) || exitAfterCodexTask || systemBusy) return;
            bool regular=FlowCreation.IsRegularOutput(root);
            if (!regular && (codexStatus==null || codexStatus.State!=CodexState.SignedIn))
            { SetStatus(CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先在设置中完成本机 Codex 登录检测。"); RefreshCodex(); return; }
            string exe=regular?null:codexStatus.Path, model=regular?null:AgentData.Options().Model;
            var cfg=FlowCreation.Config(root);
            var task=new AssetTask { Root=root,Name=FlowTaskName,Status="正在读取项目进度" };
            int startedOn=-1;
            Action<string> report=msg=>Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (assetTasks.Contains(task)) SetAssetProgress(root,msg,task.Done,task.Total,FlowTaskName);
            }));
            string error=null;
            try
            {
                BeginAssetTask(task);
                if(regular) ShowFilm(4); else ShowFlowProject();
                startedOn=pageRevision; UpdateCodexDisplay();
                await Task.Run(()=>FlowProduction.Run(root,exe,model,approval,
                    (version,analysis,work,ids,channel,mode,update,token)=>ProduceImages(root,version,analysis,work,ids,channel,mode,
                          "出图",exe,cfg,update,token).GetAwaiter().GetResult(),report,task.Cancel.Token,()=>exitAfterCodexTask,retryAttempt,
                        (done,total) => Window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!assetTasks.Contains(task)) return;
                            task.UpdateAnalysis(done,total,null);
                            SetAssetProgress(root, task.Status, total == 0 ? 0 : task.Done, total, FlowTaskName);
                        }))));
            }
            catch(OperationCanceledException) { error="制作已暂停。"; }
            catch(Exception e) { error=AgentData.Short(e.GetBaseException().Message,700); }
            finally { EndAssetTask(task); task.Cancel.Dispose(); }
            if(regular && SameProject(root,projectFolder) && pageRevision==startedOn) TryAction(()=>ShowPromptMonitor(()=>ShowFilm(4)));
            if (!regular && SameProject(root,projectFolder) && flowProjectMarker!=null && content.IsAncestorOf(flowProjectMarker)) ShowFlowProject();
            if (SameProject(root,projectFolder)) SetStatus(error ?? FlowCreation.Status(root));
            UpdateCodexDisplay(); if(exitAfterCodexTask) FinishExit();
        }
    }
}
