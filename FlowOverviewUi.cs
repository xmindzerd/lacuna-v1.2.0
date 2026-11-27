using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private Overview currentOverview;
        private void TickFlowOverview()
        {
            if (currentOverview != null) currentOverview.Update();
        }

        private void RenderFlowOverview(string root, FlowConfig cfg, FlowState s, FlowSkillPlan plan)
        {
            bool running=AssetRunning(root, FlowTaskName);
            var reviewWork=s.Status=="待人工审核" && s.Phase=="资产审核" ? FlowWork(root) : null;
            bool needsImages=reviewWork!=null && reviewWork.Items.Any(i => i.Active == null || i.Active.Path == null ||
                (i.Active.State != "已通过" && i.Active.State != "待审图"));
            var overview = new StackPanel();
            var dashboard = new Overview(this,root,cfg,s,running,needsImages);
            var page = new StackPanel();
            flowProjectMarker = page;
            content.Children.Add(page);
            page.Children.Add(dashboard.Hero);
            AnalysisVersion currentAnalysis = null;
            if (cfg.IsV2Builtin)
            {
                try { currentAnalysis = AnalysisData.Load(root).Current(AnalysisData.CurrentScript(root)); }
                catch (Exception) { }
            }
            string detail=needsImages ? "有资产需要补图。补齐后可以重新审核。" : s.Detail;
            if (needsImages) overview.Children.Add(Notice(detail));
            var actions=new WrapPanel { Margin=new Thickness(0,10,0,0) };
            if (!running && s.Status=="待续做" && s.Phase==FlowSkills.Titles[2] && ScriptAvailable() && AnalysisData.Load(root).Confirmed(AnalysisData.CurrentScript(root))!=null)
            {
                var batch=FlowWork(root).ImageBatch;
                var pending = AssetProduction.TimedOutJobs(root,batch).ToList();
                if (pending.Count > 0) overview.Children.Add(StudioText("原任务暂无回图。单独重试可能产生一次新的图片用量。",14,5));
                foreach (var target in pending)
                {
                    actions.Children.Add(ActionButton("仅重试 "+target.Name,()=>StartFlow(root,null,target.Attempt.Id),false));
                }
            }
            AnalysisVersion blockedVersion = null;
            AnalysisResult blockedAnalysis = null;
            var blocked = new List<AssetItem>();
            if (!running && s.Status == "待续做" && ScriptAvailable())
            {
                blockedVersion = AnalysisData.Load(root).Confirmed(AnalysisData.CurrentScript(root));
                if (blockedVersion != null)
                {
                    blockedAnalysis = AnalysisData.ReadResult(root, blockedVersion);
                    blocked = AssetProduction.Load(root, blockedVersion, blockedAnalysis).Items.Where(i => !AssetProduction.PromptReady(i)).ToList();
                }
            }
            AddCueCorrection(overview, root, detail, null);
            if (blocked.Count > 0)
            {
                actions.Children.Add(ActionButton("去核实并填写决定（" + blocked.Count + " 项）", delegate {
                    if (blocked.Count > 1) ShowBlockedAssetPrompts(root, blockedVersion, blockedAnalysis);
                    else {
                        var item = blocked[0];
                        ShowAssetPage(item.Id);
                        var work = AssetProduction.Load(root,blockedVersion,blockedAnalysis);
                        ShowAssetPromptRepair(root, blockedVersion, blockedAnalysis, AnalysisData.CurrentScript(root), item, AssetProduction.CurrentRepair(item,item.PromptStyle ?? work.Style));
                    }
                }, false));
                actions.Children.Add(AutoReviewButton(root, blockedVersion, blockedAnalysis, blocked, true));
            }
            else if (!running && (s.Status=="待确认费用" || (s.Status=="待人工审核" && s.Phase=="提示词审核")))
                {
                if(s.Status=="待确认费用" && cfg.Video!=null && cfg.Video.Enabled)
                    actions.Children.Add(VideoPicker(root,cfg.Video,null,ActionButton("选段生成视频",ShowVideoJobs,true)));
                else actions.Children.Add(ActionButton("审核最终提示词",()=>ShowPromptMonitor(ShowFlowProject),true));
            }
            else if (!running && s.Status=="待人工审核")
            {
                if (needsImages)
                    actions.Children.Add(ActionButton("补齐资产并继续",()=>StartFlow(root,null),true));
                else
                {
                    string key=FlowProduction.ReviewKey(root,reviewWork);
                    actions.Children.Add(ActionButton("一键确认全部资产并继续",()=>StartFlow(root,key),true));
                }
            }
            else if (!running && s.Status!="已完成")
                actions.Children.Add(ActionButton(s.Status=="创建成功" ? "开始制作" : "继续制作",()=>StartFlow(root,null),true));
            if (running)
            {
                var task = assetTasks.First(t=>t.Name==FlowTaskName && SameProject(root,t.Root));
                actions.Children.Add(ActionButton("暂停制作",()=>UiFx.SafeCancel(task.Cancel),true));
            }
            else if (s.Status=="已完成")
                actions.Children.Add(ActionButton("查看成片交付汇总",()=>ShowStage(6),true));
            dashboard.HeroBody.Children.Add(actions);
            dashboard.Actions = actions;
            dashboard.AddInspectorControls();
            dashboard.HeroBody.Children.Add(overview);
            var tools = new StackPanel();
            actions = new WrapPanel();
            tools.Children.Add(actions);
            if (!String.IsNullOrEmpty(s.Export) || IsPromptReportStage(s))
                actions.Children.Add(ActionButton("提示词监看台",()=>TryAction(()=>ShowPromptMonitor(ShowFlowProject)),false));
            if (!String.IsNullOrEmpty(s.Export))
            {

                actions.Children.Add(ActionButton("打开导出文件夹",()=>TryAction(()=>{
                    string exportDir=AgentData.Inside(root,s.Export);
                    if (!Directory.Exists(exportDir)) throw new IOException("找不到导出文件夹，可重新运行导出。");
                    using (Process.Start(new ProcessStartInfo(exportDir) { UseShellExecute=true })) { }
                }),false));
            }
            if (cfg.Video!=null && cfg.Video.Enabled && s.Status!="待确认费用") actions.Children.Add(VideoPicker(root,cfg.Video,null,ActionButton("视频生成",ShowVideoJobs,false)));
            actions.Children.Add(ActionButton("查看剧本",()=>ShowScriptReader("返回一键成片项目",ShowFlowProject,0,null),false));
            if (s.Phase != null) actions.Children.Add(ActionButton("查看资产",ShowAssetHandoff,false));
            if (s.Status=="已完成") actions.Children.Add(ActionButton("查阅分镜",()=>ShowStage(2),false));
            if (currentAnalysis != null)
            {
                string director=DirectorPipeline.DirectorMdPath(root,currentAnalysis.Id);
                string visual=DirectorPipeline.VisualMdPath(root,currentAnalysis.Id);
                if (File.Exists(director)) actions.Children.Add(ActionButton("查看导演剧本分析",()=>TryAction(()=>PreviewAnalysisMarkdown(director)),false));
                if (File.Exists(visual)) actions.Children.Add(ActionButton("查看视觉设定",()=>TryAction(()=>PreviewAnalysisMarkdown(visual)),false));
            }
            string analysisLogs = AgentData.Inside(root, @"剧本分析\运行记录");
            if (Directory.Exists(analysisLogs))
                actions.Children.Add(ActionButton("查看分析运行记录", () => TryAction(() => OpenSystemFolder(analysisLogs)), false));
            if (!running && s.Status!="已完成") {flowAccount=Muted(CodexAppServer.Summary(agentOptions),12,6); tools.Children.Add(flowAccount);}
            page.Children.Add(dashboard.Track);
            page.Children.Add(dashboard.Drawer);
            var routeCards = new System.Windows.Controls.Primitives.UniformGrid { Columns=2 };
            for (int i = 0; i < Stage.All.Length; i++)
            {
                int stage = i;
                routeCards.Children.Add(StageTile(i, SystemWord(Stage.All[i].Name), stageProgress[i], false, () => ShowStage(stage)));
            }
            tools.Children.Add(routeCards);
            if (reviewWork!=null)
            {
                var thumbs=new WrapPanel();
                foreach (var item in reviewWork.Items)
                {
                    var card=new StackPanel { Width=140 };
                    var thumb=AssetThumbnail(root,item.Source.category,item.Active);
                    var photo=thumb as Image;
                    if (photo!=null) photo.Stretch=System.Windows.Media.Stretch.Uniform;
                    var image=new Border { Width=128,Height=110,Child=thumb };
                    card.Children.Add(image); card.Children.Add(StudioText(item.Source.name,13,4));
                    thumbs.Children.Add(ActionButton(card,()=>ShowAssetPage(item.Id),false));
                }
                page.Children.Add(thumbs);
            }
            if (plan!=null && plan.routes!=null && plan.Skills!=null)
            {
                var rules=new StackPanel { Margin=new Thickness(12) };
                foreach (string stage in FlowSkills.Stages)
                {
                    var route=plan.routes.FirstOrDefault(r=>r!=null && r.stage==stage);
                    if (route==null || route.members==null) continue;
                    rules.Children.Add(Title(FlowSkills.Title(stage),15));
                    var validMembers=route.members.Where(i=>i>=1 && i<=plan.Skills.Count).ToList();
                    rules.Children.Add(StudioText(validMembers.Count==0 ? "使用内置流程\n" + route.guide :
                        String.Join(" + ",validMembers.Select(i=>plan.Skills[i-1].Name)) + "\n" + route.guide,13,4));
                }
                if (plan.Decisions!=null)
                    foreach(var d in plan.Decisions.Where(d=>d!=null && Array.IndexOf(FlowSkills.Stages,d.Stage)>=0 && d.Selected>=1 && d.Selected<=plan.Skills.Count))
                        rules.Children.Add(StudioText(FlowSkills.Title(d.Stage)+" · "+d.Point+
                            "\n采用 "+plan.Skills[d.Selected-1].Name+" · "+d.Reason,13,5));
                tools.Children.Add(new Expander { Header=cfg.Reused ? "查看制作步骤和 Skill 分配 · 已复用组合" : "查看制作步骤和 Skill 分配",
                    Content=rules, Foreground=Window.Foreground, Margin=new Thickness(0,10,0,0) });
            }
            page.Children.Add(new Expander { Header="项目工具与资料", Content=tools, Foreground=Window.Foreground,
                Margin=new Thickness(0,14,0,0) });
            currentOverview = dashboard;
            cleanupPageAnimations += delegate {
                dashboard.Stop();
                currentOverview = null;
                flowStatusHeader = null;
                flowProjectMarker = null;
            };
            dashboard.Update();
            UiFx.StaggerEntrance(new UIElement[] { dashboard.Hero, dashboard.Track, dashboard.Drawer }, Sys);
        }

        // A page owns its controls and clocks. Only the current page receives the shared pulse.
        private sealed class Overview
        {
            private readonly Workspace w;
            private readonly string root;
            private readonly FlowConfig cfg;
            private readonly FlowState state;
            private readonly bool running, needsImages, allSegmented, allPromptsReady;
            private readonly Border shimmerHost, shimmerBar;
            private bool disposed;

            private readonly AssetTask task;
            private static readonly int[] StagePages = {0,1,1,2,4,6};
            private readonly UniformGrid steps = new UniformGrid { Columns=3 };
            private readonly Step[] nodes = new Step[6];
            private readonly StackPanel rows = new StackPanel();
            private readonly TextBlock[] metrics = new TextBlock[3];
            private readonly Button[] tabs = new Button[3];
            private readonly TextBlock badge, title, detail, focus;
            private readonly ProgressBar progress;
            private Action stopShimmer;
            private readonly Button jump;
            private Button toggle, repairs;
            private int selected = -1, tab;
            private long incidentRevision = -1;
            private List<FlowIncident> incidents = new List<FlowIncident>();
            private string inspectorKey;
            private readonly Dictionary<string,WorkRow> workRows = new Dictionary<string,WorkRow>();
            internal readonly Border Hero, Drawer;
            internal readonly StackPanel Track, HeroBody = new StackPanel();
            internal WrapPanel Actions;

            private sealed class Step
            {
                internal Button Button;
                internal Border Link;
                internal TextBlock Icon, Repair, Summary, Time;
                internal string State;
            }

            private sealed class WorkRow
            {
                internal StackPanel Panel;
                internal TextBlock Title, Detail, Clock;
                internal ProgressBar Bar;
            }

            internal Overview(Workspace workspace, string folder, FlowConfig config, FlowState saved, bool isRunning, bool missingImages)
            {
                w=workspace;
                root=folder;
                cfg=config;
                state=saved;
                running=isRunning;
                needsImages=missingImages;
                if (w.stageProgress[3]!="待开始")
                {
                    try
                    {
                        var book=Film.Open(root);
                        var included=book.Scenes.Where(sc=>sc.Included).ToArray();
                        allSegmented=included.Length>0 && included.All(sc=>sc.Grouped && sc.Segments.Count>0);
                        allPromptsReady=allSegmented && included.All(sc=>sc.Segments.All(g=>FilmWriter.Current(book,sc,g)));
                    }
                    catch (Exception) { /* Keep the persisted stage summary available during recovery. */ }
                }
                task=w.assetTasks.FirstOrDefault(t=>t.Name==FlowTaskName && SameProject(root,t.Root));
                bool review = state.Status=="待人工审核" || state.Status=="待确认费用";
                bool completed = state.Status=="已完成";
                bool paused = state.Status=="已暂停" || state.Status=="制作中" && !running;
                string tone = running ? "Accent" : review ? "Review" : completed ? "Success" :
                    state.Status=="待续做" ? "Danger" : "Line";
                Hero=new Border { Child=HeroBody, Padding=new Thickness(18,16,18,16), CornerRadius=new CornerRadius(16),
                    BorderThickness=new Thickness(1), BorderBrush=w.Brush(tone), Background=Gradient("HeroStart","HeroEnd") };
                var left=new StackPanel();
                string status = running ? "自动运行中" : completed ? "全部完成" : paused ? "已暂停" :
                    state.Status=="待确认费用" ? "待确认费用" : review ? (state.Phase=="资产审核" ? "等待资产确认" : "等待提示词确认") :
                    state.Status=="待续做" ? "需关注" : "准备就绪";
                var badgeBar=new StackPanel {Orientation=Orientation.Horizontal};
                badge=w.StudioText(running ? "●" : completed ? "✓" : paused ? "⏸" : review ? "◆" : state.Status=="待续做" ? "⚠" : "○",13,0);
                badge.Foreground=w.Brush(tone=="Line" ? "TextMuted" : tone);
                badgeBar.Children.Add(badge);
                var badgeText=w.Title(status,13);
                badgeText.Foreground=badge.Foreground;
                badgeText.Margin=new Thickness(6,0,0,0);
                badgeBar.Children.Add(badgeText);
                left.Children.Add(badgeBar);
                title=w.Title(status,22);
                title.Margin=new Thickness(0,6,0,0);
                title.TextWrapping=TextWrapping.Wrap;
                left.Children.Add(title);
                detail=w.StudioText("",14,3);
                left.Children.Add(detail);
                var counters=new UniformGrid {Columns=3,VerticalAlignment=VerticalAlignment.Center};
                string[] names={"总用时","当前步骤用时","流程进度"};
                for (int i=0;i<3;i++)
                {
                    var box=new StackPanel();
                    box.Children.Add(w.Muted(names[i],12,0));
                    metrics[i]=w.Title("",21);
                    metrics[i].FontFamily=new FontFamily("Consolas");
                    metrics[i].Margin=new Thickness(0,2,0,0);
                    box.Children.Add(metrics[i]);
                    counters.Children.Add(new Border {Child=box,Background=w.Brush("Input"),
                        BorderBrush=w.Brush("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),
                        Padding=new Thickness(12,8,10,8), Margin=new Thickness(4,0,0,0)});
                }
                HeroBody.Children.Add(w.StudioColumns(left,counters,1.5,1,670));
                var tags=new WrapPanel { Margin=new Thickness(0,8,0,6) };
                foreach (var label in new[] { cfg.Ratio=="9:16" ? "竖屏 9:16" : "横屏 16:9（历史项目）", "资产图 4:3",
                    cfg.ChannelName ?? (cfg.Channel=="builtin" ? "Codex 内置生图" : cfg.Channel), cfg.Manual ? "资产人工审核" : "资产自动交接", VideoSummary(cfg.Video),
                    cfg.FinalPromptSkillName, w.project.DialogueLanguage })
                {
                    if(String.IsNullOrWhiteSpace(label)) continue;
                    var tag=w.StatusBadge(label);
                    tag.Margin=new Thickness(0,2,6,2);
                    tags.Children.Add(tag);
                }
                HeroBody.Children.Add(tags);
                w.flowStatusHeader=w.Muted("",12,2);
                HeroBody.Children.Add(w.flowStatusHeader);
                var rail=new Grid { Margin=new Thickness(0,5,0,2) };
                progress=new ProgressBar {Minimum=0, Maximum=100, Height=8, Style=w.Style("FlowProgress"),
                    Foreground=Gradient("Accent","Success"),Background=w.Brush("Input")};
                rail.Children.Add(progress);
                var light=new LinearGradientBrush {StartPoint=new Point(0,.5),EndPoint=new Point(1,.5)};
                light.GradientStops.Add(new GradientStop(Colors.Transparent,0));
                var accent=((SolidColorBrush)w.Brush("Accent")).Color;
                light.GradientStops.Add(new GradientStop(Color.FromArgb(160,accent.R,accent.G,accent.B),.5));
                light.GradientStops.Add(new GradientStop(Colors.Transparent,1));
                var sheen=new Border {Width=48,HorizontalAlignment=HorizontalAlignment.Left,Opacity=.52,Background=light};
                var clip=new Border {Child=sheen,ClipToBounds=true,IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Left};
                rail.Children.Add(clip);
                HeroBody.Children.Add(rail);
                shimmerHost=clip;
                shimmerBar=sheen;
                Hero.Loaded+=HandleLoaded;
                Hero.Unloaded+=HandleUnloaded;
                clip.Visibility=running ? Visibility.Visible : Visibility.Collapsed;
                Track=new StackPanel { Margin=new Thickness(0,14,0,10) };
                var trackTitle=w.Title("制作流程",16);
                trackTitle.Margin=new Thickness(0,0,0,4);
                Track.Children.Add(trackTitle);
                Track.Children.Add(steps);
                var snapshot=FlowTelemetry.Snapshot(root);
                for (int i=0;i<nodes.Length;i++) BuildStep(i,snapshot.Stages[i].Title);
                steps.SizeChanged+=delegate {
                    int cols=steps.ActualWidth>=950 ? 6 : steps.ActualWidth>=520 ? 3 : 2;
                    steps.Columns=cols;
                    for(int i=0;i<nodes.Length;i++) nodes[i].Link.Visibility=(i+1)%cols==0 ? Visibility.Hidden : Visibility.Visible;
                };
                var inspector=new StackPanel {Margin=new Thickness(16)};
                var nav=new WrapPanel();
                string[] labels={"正在工作","实时进程","问题与自愈"};
                for(int i=0;i<tabs.Length;i++)
                {
                    int index=i;
                    tabs[i]=w.ActionButton(labels[i],()=>SelectView(selected,index),false);
                    nav.Children.Add(tabs[i]);
                }
                inspector.Children.Add(nav);
                focus=w.Title("",16);
                focus.Margin=new Thickness(0,10,0,5);
                inspector.Children.Add(focus);
                inspector.Children.Add(new ScrollViewer {Content=rows,MaxHeight=300,
                    VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
                jump=w.ActionButton("前往该阶段工作页 ›",()=>{
                    int index=selected>=0 ? selected : FlowTelemetry.Snapshot(root).ActiveStageIndex;
                    if(selected<0 && !running && IsPromptReportStage(state)) index=5;
                    if(selected<0 && !running && state.Phase=="资产审核") index=2;
                    if(index==5 && !completed) w.ShowPromptMonitor(w.ShowFlowProject);
                    else w.ShowStage(StagePages[Math.Max(0,index)]);
                },false);
                jump.HorizontalAlignment=HorizontalAlignment.Left;
                inspector.Children.Add(jump);
                Drawer=w.Panel(inspector);
                Drawer.Visibility=running ? Visibility.Visible : Visibility.Collapsed;
            }

            private LinearGradientBrush Gradient(string start, string end)
            {
                return new LinearGradientBrush(((SolidColorBrush)w.Brush(start)).Color,
                    ((SolidColorBrush)w.Brush(end)).Color,0);
            }

            internal void AddInspectorControls()
            {
                toggle=w.ActionButton("任务详情 ▾",()=>{
                    if(Drawer.Visibility!=Visibility.Visible) SelectView(-1,tab);
                    else {Drawer.Visibility=Visibility.Collapsed; selected=-1; Update();}
                },false);
                Actions.Children.Add(toggle);
                repairs=w.ActionButton("",()=>SelectView(-1,2),false);
                Actions.Children.Add(repairs);

            }

            private void BuildStep(int index,string name)
            {
                var node=new Step();
                nodes[index]=node;
                var stack=new StackPanel();
                var line=new DockPanel();
                node.Icon=w.Title("○",16);
                node.Icon.HorizontalAlignment=HorizontalAlignment.Left;
                line.Children.Add(node.Icon);
                var number=w.Muted((index+1).ToString("D2"),12,0);
                number.Margin=new Thickness(7,0,0,0);
                number.FontFamily=new FontFamily("Consolas");
                line.Children.Add(number);
                node.Repair=w.Muted("",11,0);
                node.Repair.Foreground=w.Brush("Review");
                node.Repair.HorizontalAlignment=HorizontalAlignment.Right;
                line.Children.Add(node.Repair);
                stack.Children.Add(line);
                var text=w.Title(name,14);
                text.TextWrapping=TextWrapping.Wrap;
                text.Margin=new Thickness(0,7,0,0);
                stack.Children.Add(text);
                node.Summary=w.StudioText("",12,5);
                node.Summary.LineHeight=17;
                node.Summary.Height=34;
                node.Summary.TextTrimming=TextTrimming.CharacterEllipsis;
                stack.Children.Add(node.Summary);
                node.Time=w.Muted("",12,4);
                stack.Children.Add(node.Time);
                node.Button=w.ActionButton(stack,()=>SelectView(index,0),false);
                node.Button.Style=w.Style("FlowStepCard");
                node.Button.Margin=new Thickness(0);
                NameControl(node.Button,number.Text+" "+name);
                UiFx.AttachCardLift(node.Button,w.Sys);
                var cell=new DockPanel {Margin=new Thickness(0,6,0,2)};
                node.Link=new Border {Visibility=(index+1)%3==0 ? Visibility.Hidden : Visibility.Visible, Width=9, Height=2, VerticalAlignment=VerticalAlignment.Center};
                DockPanel.SetDock(node.Link,Dock.Right);
                cell.Children.Add(node.Link);
                cell.Children.Add(node.Button);
                steps.Children.Add(cell);
            }

            private void SelectView(int step,int nextTab)
            {
                selected=step;
                tab=nextTab;
                Drawer.Visibility=Visibility.Visible;
                inspectorKey=null;
                Update();
                UiFx.AnimateReveal(Drawer,w.Sys);
                focus.BringIntoView();
            }

            private string Summary(int i)
            {
                return i==3 ? w.stageProgress[2]+" · "+w.stageProgress[3] : w.stageProgress[StagePages[i]];
            }

            private string StageState(FlowStageSnapshot stage)
            {
                int i=stage.Index;
                bool final=IsPromptReportStage(state);
                bool assets=state.Phase=="资产审核" && state.Status=="待人工审核";
                if (i==2 && !cfg.Manual) return FlowTelemetry.StateSkipped;
                if (!running && (state.Status=="已完成" || final && i<5 || assets && (i==0 || i==1 && !needsImages)))
                    return FlowTelemetry.StateCompleted;
                if (!running && (final && i==5 || assets && i==2)) return FlowTelemetry.StateWaiting;
                if (stage.State==FlowTelemetry.StateRunning && !running) return "已暂停";
                if (stage.State!=FlowTelemetry.StatePending) return stage.State;
                int phase=Array.IndexOf(FlowSkills.Titles,state.Phase);
                if (phase>=3 && i<=2 || phase==5 && i==3) return FlowTelemetry.StateCompleted;
                if (i==3 && allSegmented || i==4 && allPromptsReady)
                    return FlowTelemetry.StateCompleted;
                if (i==0 && Summary(i).Contains("已确认") || (i==1 || i==2) && Summary(i).Contains("已交接"))
                    return FlowTelemetry.StateCompleted;
                return stage.State;
            }

            internal void Update()
            {
                var live=FlowTelemetry.Snapshot(root);
                int active=live.ActiveStageIndex;
                long elapsed=task==null ? live.TotalElapsedMs : Math.Max(live.TotalElapsedMs,task.Clock.ElapsedMilliseconds);
                string stageName=active<0 ? (state.Phase ?? "准备制作") : live.Stages[active].Title;
                title.Text=running ? "正在执行 · "+stageName : state.Status=="已完成" ? "全部制作步骤已完成" :
                    state.Status=="待确认费用" ? "提示词已确认，等待选择视频片段" :
                    state.Status=="待人工审核" ? (state.Phase=="资产审核" ? (needsImages ? "有资产需要补图" : "资产图已全部就绪，等你确认") : "最终提示词已生成，等你确认") :
                    state.Status=="创建成功" ? "准备开始制作" : state.Status=="制作中" ? "等待继续制作" : state.Status;
                detail.Text=running ? (live.ActiveItems.Length>0 && FlowTelemetry.IsActiveWork(live.ActiveItems[0].State)
                    ? live.ActiveItems[0].Title+" · "+live.ActiveItems[0].Detail
                    : live.CurrentDetail ?? (task==null ? live.CurrentHeadline : task.CountedStatus)) : state.Detail;
                w.flowStatusHeader.Text=running ? "实时推进 · 已运行 "+(elapsed/1000)+" 秒 · 点击任意步骤卡片或「任务详情」查看工作项" : "";
                w.flowStatusHeader.Visibility=running ? Visibility.Visible : Visibility.Collapsed;
                metrics[0].Text=FlowTelemetry.FormatDuration(elapsed);
                metrics[1].Text=FlowTelemetry.FormatDuration(active<0 && running ? elapsed : live.ActiveStageElapsedMs);
                if(live.IncidentRevision!=incidentRevision) {incidents=FlowIncidentStore.Query(root,FlowHalf.PostAssets,true); incidentRevision=live.IncidentRevision;}
                int fixedCount=incidents.Count(x=>x.Resolved);
                int liveWorkCount=live.ActiveItems.Count(x=>FlowTelemetry.IsActiveWork(x.State));
                tabs[0].Content=liveWorkCount>0 ? "正在工作（"+liveWorkCount+"）" : "正在工作";
                tabs[2].Content=incidents.Count>0 ? "问题与自愈（"+incidents.Count+"）" : "问题与自愈";
                repairs.Visibility=running && fixedCount>0 ? Visibility.Visible : Visibility.Collapsed;
                if(fixedCount>0) repairs.Content="⚡ 已自动解决 "+fixedCount+" 项中途问题";
                double done=0;
                int included=0;
                foreach(var stage in live.Stages)
                {
                    int i=stage.Index;
                    var node=nodes[i];
                    string status=StageState(stage);
                    bool activeStep=status==FlowTelemetry.StateRunning;
                    bool complete=status==FlowTelemetry.StateCompleted || status==FlowTelemetry.StateSkipped;
                    if(status!=FlowTelemetry.StateSkipped) included++;
                    done+=status==FlowTelemetry.StateSkipped ? 0 : complete ? 1 : status==FlowTelemetry.StateWaiting ? .85 : activeStep ?
                        (stage.Total>0 ? Math.Min(.95,(double)stage.Done/stage.Total) : .35) : 0;
                    if (node.State!=status)
                    {
                        node.State=status;
                        node.Icon.Text=status==FlowTelemetry.StateSkipped ? "⊘" : complete ? "✓" : activeStep ? "◔" :
                            status==FlowTelemetry.StateWaiting ? "◆" : status==FlowTelemetry.StateFailed ? "⚠" : "○";
                        node.Icon.Foreground=w.Brush(complete ? "Success" : activeStep ? "Accent" :
                            status==FlowTelemetry.StateFailed ? "Danger" : status==FlowTelemetry.StateWaiting ? "Review" : "TextMuted");
                        node.Button.BorderBrush=w.Brush(activeStep ? "Accent" : status==FlowTelemetry.StateWaiting ? "WarningBorder" :
                            status==FlowTelemetry.StateFailed ? "DangerBorder" : status==FlowTelemetry.StateCompleted ? "SuccessBorder" : "Line");
                        node.Button.Background=w.Brush(activeStep ? "HeroStart" : status==FlowTelemetry.StateWaiting ? "WarningTint" :
                            status==FlowTelemetry.StateFailed ? "DangerTint" : "Panel");
                        node.Time.Foreground=w.Brush(activeStep ? "AccentText" : status==FlowTelemetry.StateWaiting ? "Review" :
                            status==FlowTelemetry.StateFailed ? "Danger" : "TextMuted");
                        node.Link.Background=complete ? (Brush)Gradient("Accent","Success") : w.Brush("Line");
                        if(Hero.IsLoaded) UiFx.SetRotatingRing(node.Icon,activeStep,w.Sys);
                    }
                    node.Button.Tag=selected==i ? "selected" : null;
                    int repaired=incidents.Any(x=>x.StageIndex==i) ? incidents.Count(x=>x.StageIndex==i && x.Resolved) : stage.RepairedCount;
                    node.Repair.Text=repaired>0 ? "⚡修"+repaired : "";
                    string summary=String.IsNullOrWhiteSpace(stage.Summary) || stage.Summary=="等待上游完成" ? Summary(i) : stage.Summary;
                    node.Summary.Text=summary+(stage.Total>0 ? " · "+stage.Done+"/"+stage.Total : "");
                    node.Summary.ToolTip=node.Summary.Text;
                    node.Time.Text=stage.ElapsedMs>0 || activeStep ? (activeStep ? "进行中 " : "用时 ")+FlowTelemetry.FormatDuration(stage.ElapsedMs) : status;
                }
                double percent=included>0 ? Math.Round(done*100/included) : 0;
                metrics[2].Text=percent+"%";
                metrics[2].Foreground=w.Brush(percent>=100 ? "Success" : running ? "AccentText" : "TextMain");
                UiFx.AnimateProgress(progress,percent,w.Sys);
                toggle.Content=Drawer.Visibility==Visibility.Visible ? "任务详情 ▴" : "任务详情 ▾";
                if(Drawer.Visibility==Visibility.Visible) UpdateInspector(live);
            }

            private void UpdateInspector(FlowLiveSnapshot live)
            {
                string scope=selected<0 ? "全部步骤" : live.Stages[selected].Title;
                focus.Text=scope+(selected>=0 ? " · "+FlowTelemetry.FormatDuration(live.Stages[selected].ElapsedMs) : "");
                for(int i=0;i<tabs.Length;i++)
                {
                    tabs[i].BorderBrush=w.Brush(tab==i ? "Accent" : "Line");
                    tabs[i].Background=w.Brush(tab==i ? "SelectedNavigation" : "Panel");
                }
                jump.Visibility=tab==0 ? Visibility.Visible : Visibility.Collapsed;
                string key=selected+"/"+tab+(tab==2 ? "/"+incidentRevision : "");
                bool changed=key!=inspectorKey;
                if(changed)
                {
                    rows.Children.Clear();
                    workRows.Clear();
                    inspectorKey=key;
                }
                if(tab==0) UpdateWorkRows(live);
                else if(tab==1) UpdateTimeline(live);
                else if(changed) DrawIncidents();
            }

            private void UpdateTimeline(FlowLiveSnapshot live)
            {
                var entries=live.Timeline.Where(x=>selected<0 || x.StageIndex==selected).ToArray();
                if(entries.Length==0)
                {
                    if(rows.Children.Count>0) return;
                    foreach(var stage in live.Stages.Where(x=>!String.IsNullOrEmpty(x.FinishedUtc) &&
                        (selected<0 || x.Index==selected)).OrderByDescending(x=>x.FinishedUtc))
                    {
                        DateTime saved;
                        string time=DateTime.TryParse(stage.FinishedUtc,out saved) ? saved.ToLocalTime().ToString("HH:mm:ss") : stage.FinishedUtc;
                        rows.Children.Add(w.StudioText(time+"   ✓   "+stage.Title+" · "+stage.Summary,13,4));
                    }
                    if(rows.Children.Count==0) rows.Children.Add(w.StudioText("暂无进程事件",13,4));
                    return;
                }
                var ids=new HashSet<long>(entries.Select(e=>e.Seq));
                var visible=new HashSet<long>();
                foreach(var row in rows.Children.Cast<TextBlock>().ToArray())
                {
                    if(row.Tag is long && ids.Contains((long)row.Tag)) visible.Add((long)row.Tag);
                    else rows.Children.Remove(row);
                }
                foreach(var e in entries.Reverse().Where(e=>!visible.Contains(e.Seq)))
                {
                    var entry=w.StudioText(e.Time+"   ●   "+e.Message,13,4);
                    entry.Tag=e.Seq;
                    entry.Foreground=w.Brush(e.Level=="完成" ? "Success" : e.Level=="错误" ? "Danger" :
                        e.Level=="警告" || e.Level=="修复" ? "Review" : "TextMuted");
                    rows.Children.Insert(0,entry);
                }
            }

            private void UpdateWorkRows(FlowLiveSnapshot live)
            {
                var items=live.ActiveItems.Where(x=>(selected<0 || selected==x.StageIndex) &&
                    FlowTelemetry.IsActiveWork(x.State)).ToArray();
                var keys=new HashSet<string>(items.Select(x=>x.Key));
                foreach(var stale in workRows.Keys.Where(k=>!keys.Contains(k)).ToArray())
                {
                    rows.Children.Remove(workRows[stale].Panel);
                    workRows.Remove(stale);
                }
                var empty=rows.Children.OfType<TextBlock>().FirstOrDefault();
                if(items.Length==0)
                {
                    if(empty==null) {empty=w.StudioText("",14,4); rows.Children.Add(empty);}
                    empty.Text=selected>=0 ? nodes[selected].Summary.Text : running && task!=null ? task.CountedStatus :
                        String.Join("  ·  ",new[]{Summary(0),Summary(1),Summary(3),Summary(4)}.Distinct());
                    return;
                }
                if(empty!=null) rows.Children.Remove(empty);
                foreach(var item in items)
                {
                    WorkRow row;
                    if(!workRows.TryGetValue(item.Key,out row))
                    {
                        row=new WorkRow {Panel=new StackPanel {Margin=new Thickness(0,4,0,5)},Title=w.Title("",14),
                            Detail=w.StudioText("",13,2), Clock=w.Muted("",12,0),
                            Bar=new ProgressBar {Height=3,Maximum=100,Style=w.Style("FlowProgress"),Foreground=w.Brush("Accent"),Background=w.Brush("Input")} };
                        var header=new DockPanel();
                        DockPanel.SetDock(row.Clock,Dock.Right);
                        header.Children.Add(row.Clock);
                        header.Children.Add(row.Title);
                        row.Panel.Children.Add(header);
                        row.Panel.Children.Add(row.Detail);
                        row.Panel.Children.Add(row.Bar);
                        rows.Children.Add(row.Panel);
                        workRows.Add(item.Key,row);
                    }
                    row.Title.Text="●  "+item.Title;
                    row.Title.Foreground=w.Brush(item.State=="运行中" ? "AccentText" : "Review");
                    row.Detail.Text=item.Detail;
                    row.Clock.Text=item.State+" · "+FlowTelemetry.FormatDuration(item.FinalElapsedMs);
                    row.Bar.Visibility=item.Total>0 ? Visibility.Visible : Visibility.Collapsed;
                    row.Bar.ToolTip=item.Total>0 ? item.Done+" / "+item.Total : "执行中，尚无可计数进度";
                    UiFx.AnimateProgress(row.Bar,item.Total>0 ? 100.0*item.Done/item.Total : 0,w.Sys);
                }
            }

            private void DrawIncidents()
            {
                var list=incidents.Where(x=>selected<0 || x.StageIndex==selected).ToList();
                foreach(var incident in list)
                {
                    rows.Children.Add(w.Title(incident.StageTitle+" · "+incident.Summary,14));
                    rows.Children.Add(w.StudioText((incident.Resolved ? "已自动解决" : "待人工处理")+" · "+
                        FlowTelemetry.FormatDuration(incident.ResolutionMs)+"  "+incident.CodexFixAction,13,3));
                    if(!String.IsNullOrEmpty(incident.PermanentFixSuggestion))
                        rows.Children.Add(w.StudioText("永久修复建议 · "+incident.PermanentFixSuggestion,13,3));
                }
                if(list.Count==0)
                {
                    var empty=w.StudioText(incidents.Count==0 ? "当前流程暂无问题记录" : "本步骤暂无问题记录",14,5);
                    empty.Foreground=w.Brush("Success");
                    rows.Children.Add(empty);
                }
            }

            private void HandleLoaded(object sender,RoutedEventArgs e)
            {
                if(disposed || !running) return;
                if(stopShimmer!=null) stopShimmer();
                progress.ApplyTemplate();
                var indicator=progress.Template.FindName("PART_Indicator",progress) as FrameworkElement;
                if(indicator!=null)
                    shimmerHost.SetBinding(FrameworkElement.WidthProperty,new System.Windows.Data.Binding("ActualWidth"){Source=indicator});
                stopShimmer=UiFx.AttachShimmer(shimmerHost,shimmerBar,w.Sys);
                UiFx.SetPulse(badge,true,w.Sys);
                foreach(var node in nodes) UiFx.SetRotatingRing(node.Icon,node.State==FlowTelemetry.StateRunning,w.Sys);
            }

            private void HandleUnloaded(object sender,RoutedEventArgs e)
            {
                if(stopShimmer!=null) {stopShimmer(); stopShimmer=null;}
                System.Windows.Data.BindingOperations.ClearBinding(shimmerHost,FrameworkElement.WidthProperty);
                UiFx.SetPulse(badge,false,w.Sys);
                foreach(var node in nodes) UiFx.SetRotatingRing(node.Icon,false,w.Sys);
            }

            internal void Stop()
            {
                disposed=true;
                Hero.Loaded-=HandleLoaded;
                Hero.Unloaded-=HandleUnloaded;
                HandleUnloaded(null,null);
            }
        }

        private static bool IsPromptReportStage(FlowState state)
        {
            return state.Status == "待确认费用" || state.Status == "已完成" ||
                (state.Status == "待人工审核" && state.Phase == "提示词审核");
        }

    }
}
