using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        const string VideoLane="视频生成";
        FrameworkElement videoPageMarker;

        void ShowVideoJobs()
        {
            TryAction(()=>DrawVideoJobs(projectFolder));
        }
        void DrawVideoJobs(string root)
        {
            BeginPage(1,"视频生成",DisplayName(),6);
            var cfg=FlowCreation.Config(root);
            if(cfg==null || cfg.Video==null || !cfg.Video.Enabled){
                if(cfg!=null && cfg.Video!=null) content.Children.Add(ActionButton("调整视频模型与分辨率",()=>TryAction(()=>ChangeVideoOptions(root)),false));
                content.Children.Add(Notice("当前项目设置为只生成提示词。可在新建项目时或视频设置中开启即梦视频生成。"));
                return;
            }
            var book=Film.Open(root);
            var clips=VideoDelivery.Plan(root,book,cfg.Video,null,(s,g)=>FilmWriter.Current(book,s,g),true);
            if(book.RatioMismatchNote!=null)content.Children.Add(Notice(book.RatioMismatchNote));
            var actions=new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            videoPageMarker=actions;content.Children.Add(actions);
            var list=new StackPanel();var picked=new List<CheckBox>();
            bool busy=AssetRunning(root,VideoLane) || AssetRunning(root,FlowTaskName);
            var receipts=clips.ToDictionary(c=>c.Key,c=>NovelVideo.Read(root,c.Key));
            var fresh=clips.Where(c=>receipts[c.Key]==null).ToList();
            var unresolved=clips.Where(c=>receipts[c.Key]!=null && !NovelVideo.Returned(root,receipts[c.Key]) && receipts[c.Key].Status!="平台失败").ToList();
            bool canGenerate=!busy && book.RatioMismatchNote==null;
            string single=null,mode="all";
            Func<List<VideoClip>> targets=delegate
            {
                if(mode=="one")return fresh.Where(c=>c.ContentKey==single).ToList();
                if(mode=="selected")return fresh.Where(c=>picked.Any(p=>p.IsChecked==true && (string)p.Tag==c.ContentKey)).ToList();
                return fresh;
            };
            Action<bool> price;
            var quotePanel=QuotePanel(root,cfg,book,targets,c=>PickerChoice(mode=="one"?SegmentChoiceKey(c):null,cfg.Video),out price);
            Action<string,string,bool> select=delegate(string scope,string key,bool accept)
            {
                mode=scope;single=key;
                if(canGenerate)price(accept);
            };
            var all=ActionButton("一键生成全部待生成片段（"+fresh.Count+"）",()=>select("all",null,true),true);
            all.IsEnabled=canGenerate && fresh.Count>0;
            actions.Children.Add(VideoPicker(root,cfg.Video,null,all,()=>select("all",null,false)));
            var selected=ActionButton("生成勾选片段",()=>select("selected",null,true),false);
            selected.IsEnabled=false;
            bool bulkSelecting=false;
            var selectAll=ActionButton("全选待生成",delegate
            {
                bulkSelecting=true;
                try{foreach(var check in picked.Where(c=>c.IsEnabled))check.IsChecked=true;}
                finally{bulkSelecting=false;}
                selected.IsEnabled=picked.Any(c=>c.IsChecked==true);
                select("selected",null,false);
            },false);
            selectAll.IsEnabled=all.IsEnabled;actions.Children.Add(selectAll);
            actions.Children.Add(VideoPicker(root,cfg.Video,null,selected,()=>select("selected",null,false)));
            if(canGenerate)content.Children.Add(quotePanel);
            if(unresolved.Count>0){
                var query=ActionButton("查询平台任务状态（"+unresolved.Count+"）",()=>RunVideos(root,unresolved.Select(c=>c.Key).ToArray(),true),false);
                query.IsEnabled=!busy;actions.Children.Add(query);
            }
            var settings=new WrapPanel();content.Children.Add(settings);
            settings.Children.Add(ActionButton("提示词监看台",()=>ShowPromptMonitor(ShowVideoJobs),false));
            if(!busy && receipts.Values.All(r=>r==null))
                settings.Children.Add(ActionButton("调整视频模型与分辨率",()=>TryAction(()=>ChangeVideoOptions(root)),false));
            var returned = clips.Where(c => NovelVideo.Returned(root, receipts[c.Key])).ToList();
            StageStatus(6,"已取回 "+returned.Count+"/"+book.Scenes.Where(s=>s.Included).Sum(s=>s.Segments.Count)+" 段");
            if (returned.Count > 0)
            {
                var firstClip = receipts[returned[0].Key];
                settings.Children.Add(ActionButton("打开回片文件夹", () => OpenClipFile(root, firstClip, true), false));
            }
            settings.Children.Add(ActionButton("即梦短剧账号授权",()=>TryAction(()=>ShowNovelAuthorization()),false));
            int count=book.Scenes.Where(s=>s.Included).Sum(s=>s.Segments.Count);
            content.Children.Add(StudioText("已定稿 "+clips.Count+" / "+count+" 段　·　选定后将向即梦取得本次积分报价",14,6));
            if(busy)AddAssetProgress(root);
            content.Children.Add(list);
            foreach(var clip in clips){
                var r=receipts[clip.Key];
                var row=new StackPanel{Margin=new Thickness(14,12,14,10)};
                var rowActions=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};
                rowActions.Children.Add(ActionButton("查看提示词",()=>ShowPromptMonitor(ShowVideoJobs,clip.Scene,clip.First),false));
                if(r==null){
                    var one=ActionButton(cfg.Video.Review?"审核并生成本段":"生成本段",()=>select("one",clip.ContentKey,true),false);
                    one.IsEnabled=canGenerate;
                    rowActions.Children.Add(VideoPicker(root,cfg.Video,SegmentChoiceKey(clip),one,()=>select("one",clip.ContentKey,false)));
                }else if(NovelVideo.Returned(root,r))
                {
                    rowActions.Children.Add(ActionButton("监看视频",()=>PlayClip(root,r),false));
                    rowActions.Children.Add(ActionButton("打开原视频",()=>OpenClipFile(root,r,false),false));
                    rowActions.Children.Add(ActionButton("打开所在文件夹",()=>OpenClipFile(root,r,true),false));
                }
                else if(!busy && (r.Status=="平台失败" || r.Status=="待核对" || r.Provider!="novel" && !VideoDelivery.ValidId(r.ExternalId)))
                    rowActions.Children.Add(ActionButton("核对原任务",()=>TryAction(()=>ResolveVideoReceipt(root,r)),false));


                var leftInfo=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,0)};
                var titleLine=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
                var check=new CheckBox{Content=clip.Title,Tag=clip.ContentKey,IsEnabled=canGenerate && r==null,
                    VerticalAlignment=VerticalAlignment.Center,Foreground=Window.Foreground,FontWeight=FontWeights.SemiBold};
                picked.Add(check);
                RoutedEventHandler changed=delegate
                {
                    if(bulkSelecting)return;
                    selected.IsEnabled=picked.Any(x=>x.IsChecked==true);
                    select("selected",null,false);
                };
                check.Checked+=changed;check.Unchecked+=changed;
                titleLine.Children.Add(check);
                string clipStatus=r==null?"待生成":r.Status=="已提交" && String.IsNullOrWhiteSpace(r.ExternalId)?"待核对":r.Status;
                string badgeTone=r!=null && NovelVideo.Returned(root,r)?"Accent":(clipStatus=="待核对" || clipStatus=="平台失败"?"Review":"Default");
                var statusPill=StatusBadge(clipStatus,badgeTone);
                statusPill.Margin=new Thickness(10,0,0,0);
                titleLine.Children.Add(statusPill);
                leftInfo.Children.Add(titleLine);
                var metaLine=Muted(clip.Seconds+" 秒  ·  "+clip.Images.Count+" 张参考图"+
                    (r==null?"":"  ·  "+ReceiptChoice(r))+
                    (r!=null && !String.IsNullOrWhiteSpace(r.ExternalId)?"  ·  即梦任务号 "+r.ExternalId:""),12.5,5);
                metaLine.Margin=new Thickness(27,5,0,0);
                leftInfo.Children.Add(metaLine);
                row.Children.Add(leftInfo);row.Children.Add(rowActions);

                var card=new StackPanel();card.Children.Add(row);
                if(r!=null && !String.IsNullOrWhiteSpace(r.SubmitError)){
                    var se=StudioText("提交反馈 · "+r.SubmitError,12.5,2);se.Margin=new Thickness(41,0,14,8);card.Children.Add(se);
                }
                if(r!=null && !String.IsNullOrWhiteSpace(r.Error)){
                    var err=StudioText(r.Error,12.5,2);err.Foreground=Brush("Review");err.Margin=new Thickness(41,0,14,8);card.Children.Add(err);
                }
                var cardBorder = Panel(card);
                cardBorder.Margin = new Thickness(0, 0, 0, 10);
                list.Children.Add(cardBorder);
            }
            if(canGenerate && fresh.Count>0)price(false);
            if(clips.Count==0)list.Children.Add(Notice("分段和提示词定稿后，可在这里选择要生成的片段。"));
        }

        void ChangeVideoOptions(string root)
        {
            var cfg=FlowCreation.Config(root);
            if(cfg==null || cfg.Video==null)throw new InvalidOperationException("当前项目缺少视频生成配置。");
            var draft=new FlowDraft{Ratio=Film.Open(root).Brief.Ratio,Video=new VideoOptions{Platform=cfg.Video.Platform,Model=cfg.Video.Model,Resolution=cfg.Video.Resolution,Review=cfg.Video.Review}};
            var box=StyleDialog(new Window{Owner=Window,Title="视频生成设置",Width=480,SizeToContent=SizeToContent.Height,
                MaxHeight=SystemParameters.WorkArea.Height-48,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("Panel"),ShowInTaskbar=false});
            var p=new StackPanel{Margin=new Thickness(22)};
            box.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            p.Children.Add(Title("视频生成设置", 20));
            var save=ActionButton("保存视频参数",()=>TryAction(()=>{
                SaveVideoOptions(root,draft.Video);
                box.DialogResult=true;
            }),true);
            AddVideoSettings(p,draft,save);
            var btnBar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            btnBar.Children.Add(ActionButton("取消",()=>{box.DialogResult=false;},false));
            btnBar.Children.Add(save);
            p.Children.Add(btnBar);
            if(box.ShowDialog()==true) { if(draft.Video.Enabled) ShowVideoJobs(); else ShowOverview(); }
        }

        internal static void SaveVideoOptions(string root, VideoOptions options)
        {
            using (FlowCreation.Lock(root))
            using (new ProjectTaskLease(root,"影片制作"))
            {
                var cfg = FlowCreation.Config(root);
                var book = Film.Open(root);
                VideoDelivery.RequireAlignedRatio(root,book.Brief.Ratio);
                var before = VideoDelivery.Plan(root,book,cfg.Video,null,(s,g)=>FilmWriter.Current(book,s,g),true);
                string receiptDir = AgentData.Inside(root,VideoDelivery.Folder);
                if (before.Any(c => NovelVideo.Read(root,c.Key)!=null) || Directory.Exists(receiptDir) &&
                    Directory.EnumerateDirectories(receiptDir).Any(dir => File.Exists(Path.Combine(dir,"任务.json"))))
                    throw new InvalidOperationException("已有片段提交，请保留原参数查询。");
                if (options.Enabled)
                    VideoDelivery.Plan(root,book,options,VideoPlatform.For(options,book.Brief.Ratio,CancellationToken.None),(s,g)=>FilmWriter.Current(book,s,g),true);
                else options.Platform="prompt";
                var original=cfg.Video;
                cfg.Video=options;
                bool regular=FlowCreation.IsRegularOutput(root);
                var state=regular ? null : FlowCreation.State(root);
                string path=AgentData.Inside(root,regular?FlowCreation.RegularConfig:FlowCreation.ConfigFile);
                AgentData.Save(path,cfg);
                if(!options.Enabled && state!=null && state.Status=="待确认费用") {
                    state.Status="已完成"; state.Phase=FlowSkills.Titles[5]; state.Detail="已切换为只生成提示词，最终提示词已保存。";
                    try { FlowCreation.Save(root,state); }
                    catch { cfg.Video=original; AgentData.Save(path,cfg); throw; }
                }
            }
        }

        private static string FormatTimecode(TimeSpan t)
        {
            return ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
        }

        void OpenClipFile(string root, ClipReceipt receipt, bool folder)
        {
            TryAction(delegate {
                if (receipt == null || String.IsNullOrWhiteSpace(receipt.Output))
                    throw new FileNotFoundException("该片段尚无本地回片文件。");
                string path = AgentData.Inside(root, receipt.Output);
                if (!String.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new FileNotFoundException("找不到原视频，请重新核对回片。");
                if (folder)
                {
                    using (Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false })) { }
                }
                else
                {
                    using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                }
            });
        }

        void PlayClip(string root,ClipReceipt receipt)
        {
            TryAction(()=>{
                if(receipt==null || String.IsNullOrWhiteSpace(receipt.Output))throw new FileNotFoundException("该片段尚无本地回片文件。");
                string file=AgentData.Inside(root,receipt.Output);
                if(!File.Exists(file))throw new FileNotFoundException("找不到本地回片文件：" + Path.GetFileName(file));
                var player=new MediaElement{LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Close,Stretch=Stretch.Uniform};
                var box=StyleDialog(new Window{Owner=Window,Title=receipt.Title+" · 视频监看",Width=900,Height=620,
                    WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("Input"),ShowInTaskbar=false});
                var dock=new DockPanel{Margin=new Thickness(16)};box.Content=dock;
                var controls=new StackPanel{Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(controls,Dock.Bottom);dock.Children.Add(controls);
                var timeline=new Slider{Minimum=0,Maximum=Math.Max(1,receipt.Seconds),IsEnabled=false,Margin=new Thickness(0,0,0,8)};
                controls.Children.Add(timeline);
                var bar=new DockPanel();
                var buttons=new WrapPanel();
                var timeLabel=DeskLabel("00:00 / " + FormatTimecode(TimeSpan.FromSeconds(Math.Max(0, receipt.Seconds))));
                timeLabel.VerticalAlignment=VerticalAlignment.Center;
                AddRight(bar, timeLabel);
                var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
                buttons.Children.Add(ActionButton("播放",()=>{player.Play();timer.Start();},true));
                buttons.Children.Add(ActionButton("暂停",()=>{player.Pause();timer.Stop();},false));
                buttons.Children.Add(ActionButton("打开原视频",()=>{player.Pause();timer.Stop();OpenClipFile(root,receipt,false);},false));
                buttons.Children.Add(ActionButton("打开所在文件夹",()=>OpenClipFile(root,receipt,true),false));
                bar.Children.Add(buttons);
                controls.Children.Add(bar);
                var note=StudioText(receipt.Title+"　·　"+receipt.Seconds+" 秒",13,6);controls.Children.Add(note);
                var screenFrame=new Border{Background=Brush("AppBackground"),CornerRadius=new CornerRadius(10),ClipToBounds=true,Child=player};
                dock.Children.Add(screenFrame);
                bool ticking=false, dragging=false;
                timeline.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                    new System.Windows.Controls.Primitives.DragStartedEventHandler(delegate { dragging=true; }));
                timeline.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                    new System.Windows.Controls.Primitives.DragCompletedEventHandler(delegate {
                        dragging=false;
                        if(timeline.IsEnabled)player.Position=TimeSpan.FromSeconds(timeline.Value);
                    }));
                timer.Tick+=delegate{
                    if(dragging)return;
                    ticking=true;
                    timeline.Value=player.Position.TotalSeconds;
                    var total=player.NaturalDuration.HasTimeSpan?player.NaturalDuration.TimeSpan:TimeSpan.FromSeconds(Math.Max(0,receipt.Seconds));
                    timeLabel.Text=FormatTimecode(player.Position)+" / "+FormatTimecode(total);
                    ticking=false;
                };
                timeline.ValueChanged+=delegate{
                    if(!ticking && timeline.IsEnabled){
                        if(!dragging)player.Position=TimeSpan.FromSeconds(timeline.Value);
                        var total=player.NaturalDuration.HasTimeSpan?player.NaturalDuration.TimeSpan:TimeSpan.FromSeconds(Math.Max(0,receipt.Seconds));
                        timeLabel.Text=FormatTimecode(TimeSpan.FromSeconds(timeline.Value))+" / "+FormatTimecode(total);
                    }
                };
                player.MediaOpened+=delegate{
                    if(player.NaturalDuration.HasTimeSpan){
                        timeline.Maximum=player.NaturalDuration.TimeSpan.TotalSeconds;
                        timeline.IsEnabled=true;
                        timeLabel.Text="00:00 / "+FormatTimecode(player.NaturalDuration.TimeSpan);
                    }
                    timer.Start();
                };
                player.MediaFailed+=(s,e)=>{timer.Stop();note.Text="播放失败 · "+(e.ErrorException==null?"未知解码错误":e.ErrorException.Message);};
                player.MediaEnded+=delegate{timer.Stop();player.Pause();player.Position=TimeSpan.Zero;ticking=true;timeline.Value=0;ticking=false;};
                box.Closed+=delegate{timer.Stop();player.Stop();player.Close();player.Source=null;};
                box.Loaded+=delegate{player.Source=FileUri(file);player.Play();};
                box.ShowDialog();
            });
        }

        bool AcceptQuote(VideoQuote q)
        {
            var box=StyleDialog(new Window{Owner=Window,Title="确认即梦平台报价",Width=620,SizeToContent=SizeToContent.Height,
                MaxHeight=SystemParameters.WorkArea.Height-60,WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Background=Brush("Panel"),ShowInTaskbar=false});
            var p=new StackPanel{Margin=new Thickness(24)};box.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            p.Children.Add(Title(q.ProjectName+" · 即梦报价 · "+q.Items.Count+" 段",22));
            p.Children.Add(StudioText("成片画幅　"+q.Ratio,14,6));
            var listPanel=new StackPanel{Margin=new Thickness(14,10,14,10)};
            foreach(var item in q.Items){
                var row=new DockPanel{Margin=new Thickness(0,4,0,4)};
                var cr=DeskLabel(item.Credits+" 积分");AddRight(row,cr);
                row.Children.Add(Text(item.Title+" · "+item.Model+" · "+Quality(item.Resolution),13.5,0));
                listPanel.Children.Add(row);
            }
            var listBox=new Border{Background=Brush("Input"),BorderBrush=Brush("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Margin=new Thickness(0,12,0,12),Child=listPanel};
            p.Children.Add(listBox);
            p.Children.Add(Title("本次预计消耗　"+q.Total+" 积分",18));
            p.Children.Add(StudioText("当前余额　"+q.Balance+" 积分　　　预计剩余　"+q.Remaining+" 积分",14,6));
            if(q.Remaining<0)p.Children.Add(Notice("余额不足，请调整选段或补足积分后重新报价。"));
            var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};p.Children.Add(actions);
            actions.Children.Add(ActionButton("取消",()=>{box.DialogResult=false;},false));
            var confirm=ActionButton("确认生成 "+q.Items.Count+" 段",()=>{box.DialogResult=true;},true);
            confirm.IsEnabled=q.Remaining>=0;actions.Children.Add(confirm);
            return box.ShowDialog()==true;
        }

        async void RunVideos(string root,string[] keys,bool resume,VideoQuote accepted=null)
        {
            if(keys.Length==0 || AssetRunning(root,VideoLane) || AssetRunning(root,FlowTaskName))return;
            var task=new AssetTask{Root=root,Name=VideoLane,Status=resume?"查询平台任务":"准备平台草稿"};
            string error=null;
            Action<string> report=msg=>Window.Dispatcher.BeginInvoke(new Action(()=>SetAssetProgress(root,msg,0,0,VideoLane)));
            try{
                BeginAssetTask(task);ShowVideoJobs();UpdateCodexDisplay();
                int confirmationPage=pageRevision;
                bool needsNovel=!resume || keys.Any(k=>{var receipt=NovelVideo.Read(root,k);return receipt!=null && receipt.Provider=="novel";});
                bool authorized=!needsNovel || await Task.Run(()=>NovelAuth.Connected(NovelVideo.Find(),task.Cancel.Token));
                task.Cancel.Token.ThrowIfCancellationRequested();
                Func<bool> onVideoPage = () => pageRevision == confirmationPage && videoPageMarker != null && content.Children.Contains(videoPageMarker);
                Action reopenVideo = () => { ShowVideoJobs(); confirmationPage = pageRevision; };
                if(!authorized) authorized = await ProjectDecision(Path.GetFileName(root), "即梦账号授权", onVideoPage,
                    reopenVideo, () => ShowNovelAuthorization(true) ? 1 : -1, task.Cancel.Token) == 1;
                if(!authorized)error="即梦账号尚未连接，本次操作已停止。";
                else await Task.Run(()=>RunVideoSelection(root,keys,resume,report,null,task.Cancel.Token,accepted));
            }catch(OperationCanceledException){error="已停止本次操作，可继续查询已提交的任务。";}
            catch(Exception e){error=e.GetBaseException().Message;}
            finally{EndAssetTask(task);task.Cancel.Dispose();}
            if(SameProject(root,projectFolder)){
                if(videoPageMarker!=null && content.Children.Contains(videoPageMarker))ShowVideoJobs();
                SetStatus(error??FlowCreation.Status(root));
            }
            UpdateCodexDisplay();if(exitAfterCodexTask)FinishExit();
        }

        internal static void RunVideoSelection(string root,string[] keys,bool resume,Action<string> report,Func<VideoQuote,bool> confirm,CancellationToken token,VideoQuote accepted=null)
        {
            if(!resume && accepted==null)
            {
                var config=FlowCreation.Config(root);
                if(config==null || config.Video==null || !config.Video.Enabled)throw new InvalidOperationException("项目设置为只生成提示词。");
                var before=Film.Open(root);
                VideoDelivery.RequireAlignedRatio(root,before.Brief.Ratio);
                var planned=VideoDelivery.Plan(root,before,config.Video,null,(s,g)=>FilmWriter.Current(before,s,g),true)
                    .Where(c=>keys.Contains(c.Key)).ToList();
                if(keys.Length==0 || planned.Count!=keys.Length)throw new InvalidOperationException("所选定稿已变化，请重新报价。");
                accepted=new NovelVideo(root,token).Prepare(config.Video,before.Brief.Ratio,planned,report);
                if(confirm==null || !confirm(accepted))return;
                keys=accepted.Items.Select(i=>i.ContentKey).ToArray();
            }
            using(FlowCreation.Lock(root))
            using(new ProjectTaskLease(root,"影片制作")){
                var cfg=FlowCreation.Config(root);
                if(cfg==null || cfg.Video==null || !cfg.Video.Enabled)throw new InvalidOperationException("项目设置为只生成提示词。");
                var book=Film.Open(root);
                var all=VideoDelivery.Plan(root,book,cfg.Video,null,(s,g)=>FilmWriter.Current(book,s,g),true);
                List<VideoClip> chosen;
                if(resume)chosen=all.Where(c=>keys.Contains(c.Key)).ToList();
                else
                {
                    VideoDelivery.RequireAlignedRatio(root,book.Brief.Ratio);
                    if(book.Revision!=accepted.FilmRevision || !keys.SequenceEqual(accepted.Items.Select(i=>i.ContentKey)))
                        throw new InvalidDataException("定稿或所选范围已变化，请重新报价。");
                    var choices=accepted.Items.ToDictionary(i=>i.ContentKey,i=>new VideoClipChoice{Model=i.Model,Resolution=i.Resolution});
                    chosen=VideoDelivery.PlanForSelections(root,book,cfg.Video,choices);
                }
                if(keys.Length==0 || chosen.Count!=keys.Length)throw new InvalidOperationException("所选片段或定稿已变化，请刷新后再选。");
                var novel=chosen.Any(c=>!resume || (NovelVideo.Read(root,c.Key)??new ClipReceipt()).Provider=="novel")?new NovelVideo(root,token):null;
                if(!resume)
                {
                    token.ThrowIfCancellationRequested();
                    novel.SubmitLocked(accepted,chosen,report);
                    foreach(var clip in chosen)all.Single(c=>c.ContentKey==clip.ContentKey).Key=clip.Key;
                }
                if(novel!=null)novel.Collect(chosen,book.Brief.Ratio,cfg.Video.Resolution,report);
                var old=chosen.Where(c=>{var r=NovelVideo.Read(root,c.Key);return r!=null && r.Provider!="novel" && !NovelVideo.Returned(root,r);}).ToList();
                if(old.Count>0)VideoDelivery.RecoverLegacy(root,VideoPlatform.Load(token),cfg.Video,book.Brief.Ratio,old,report,token,token.ThrowIfCancellationRequested);
                int total=book.Scenes.Where(s=>s.Included).Sum(s=>s.Segments.Count);
                int done=all.Count(c=>NovelVideo.Returned(root,NovelVideo.Read(root,c.Key)));
                var state=FlowCreation.State(root);state.Phase="视频生成";state.Status=done==total?"已完成":"待确认费用";
                state.Detail="已取回 "+done+" / "+total+" 段视频。其余片段可在视频生成页查看。";
                FlowCreation.Save(root,state);report(state.Detail);
            }
        }
    }
}

