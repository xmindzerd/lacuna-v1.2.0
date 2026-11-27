using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OneClickFilm
{
    public sealed class VideoClipChoice
    {
        public string Model, Resolution;
    }

    public sealed class VideoSelectionDraft
    {
        public string BatchModel, BatchResolution;
        public Dictionary<string,VideoClipChoice> PerSegment=new Dictionary<string,VideoClipChoice>();
        internal const string FileName=@"影片制作\视频模型草稿.json";

        internal static VideoSelectionDraft Load(string root,VideoOptions defaults)
        {
            var saved=AgentData.Read<VideoSelectionDraft>(AgentData.Inside(root,FileName)) ?? new VideoSelectionDraft();
            if(String.IsNullOrWhiteSpace(saved.BatchModel))
            {
                saved.BatchModel=VideoPlatform.Canonical(defaults.Model);
                saved.BatchResolution=defaults.Resolution;
            }
            if(saved.PerSegment==null)saved.PerSegment=new Dictionary<string,VideoClipChoice>();
            return saved;
        }

        internal VideoClipChoice Choice(string segment,VideoOptions defaults)
        {
            if(segment==null)return new VideoClipChoice{Model=BatchModel,Resolution=BatchResolution};
            VideoClipChoice choice;
            return PerSegment.TryGetValue(segment,out choice) && choice!=null?choice:
                new VideoClipChoice{Model=VideoPlatform.Canonical(defaults.Model),Resolution=defaults.Resolution};
        }

        internal static VideoSelectionDraft Save(string root,VideoOptions defaults,string segment,VideoClipChoice choice)
        {
            string path=AgentData.Inside(root,FileName);
            using(var mutex=new Mutex(false,@"Local\LacunaVideoChoices-"+AgentData.Hash(path.ToUpperInvariant())))
            {
                try{mutex.WaitOne();}catch(AbandonedMutexException){}
                try
                {
                    var draft=Load(root,defaults);
                    if(segment==null){draft.BatchModel=choice.Model;draft.BatchResolution=choice.Resolution;}
                    else draft.PerSegment[segment]=choice;
                    AgentData.Save(path,draft);
                    return draft;
                }
                finally{mutex.ReleaseMutex();}
            }
        }
    }

    internal sealed class VideoQuoteState
    {
        internal long Generation;
        internal string Signature;
        internal VideoQuote Current;

        internal long Change(string signature)
        {
            Current=null;
            Signature=signature;
            return ++Generation;
        }

        internal bool Matches(long generation,string signature)
        {
            return generation==Generation && signature==Signature;
        }

        internal bool Receive(long generation,string signature,VideoQuote result)
        {
            if(!Matches(generation,signature))return false;
            Current=result;
            return true;
        }
    }

    internal sealed partial class Workspace
    {
        const string FixedQuality="模型默认（固定画质）";
        VideoSelectionDraft pickerDraft;
        int pickerPage=-1;
        VideoPlatform pickerCatalog;
        Action refreshPickers;
        Action revalidateQuote;
        CancellationTokenSource quoteWork;

        static string SegmentChoiceKey(VideoClip clip){return clip.Scene+":"+clip.First;}
        static string Quality(string size){return String.IsNullOrEmpty(size)?FixedQuality:size;}
        static string ReceiptChoice(ClipReceipt r)
        {
            if(String.IsNullOrWhiteSpace(r.Model))return "原模型未记录"+(String.IsNullOrEmpty(r.Resolution)?"":" · "+r.Resolution);
            if(r.ContentKey==null && String.IsNullOrWhiteSpace(r.Resolution) && !VideoPlatform.IsFixedQuality(r.Model))
                return r.Model+" · 原分辨率未记录";
            return r.Model+" · "+Quality(r.Resolution);
        }

        StackPanel QuotePanel(string root,FlowConfig cfg,FilmBook book,Func<List<VideoClip>> targets,
            Func<VideoClip,VideoClipChoice> choice,out Action<bool> request)
        {
            var panel=new StackPanel{Margin=new Thickness(4,6,4,10)};
            var details=StudioText("选择片段后取得本次报价。",14,4);
            NameControl(details,"本次视频报价");panel.Children.Add(details);
            var actions=new WrapPanel();panel.Children.Add(actions);
            var state=new VideoQuoteState();
            int page=pageRevision;
            CancellationTokenSource panelWork=null;
            Func<List<VideoClip>,Dictionary<string,VideoClipChoice>> choices=list=>list.ToDictionary(c=>c.ContentKey,c=>choice(c));
            Func<List<VideoClip>,string> signature=list=>AssetProduction.StableJson(list.Select(c=>new {c.ContentKey,Choice=choice(c)}).ToArray());
            Func<bool> active=()=>page==pageRevision && state.Signature==signature(targets());
            Action accept=delegate
            {
                var q=state.Current;
                if(q==null || !active() || q.Remaining<0)return;
                if(AcceptQuote(q) && active() && Object.ReferenceEquals(q,state.Current))
                    RunVideos(root,q.Items.Select(i=>i.ContentKey).ToArray(),false,q);
            };
            var confirm=ActionButton("核对并确认本次报价",accept,true);
            confirm.IsEnabled=false;actions.Children.Add(confirm);
            Action<bool> refresh=null;
            var retry=ActionButton("重新询价",()=>refresh(false),false);
            retry.Visibility=Visibility.Collapsed;actions.Children.Add(retry);
            var authorize=ActionButton("即梦短剧账号授权",delegate
            {
                long generation=state.Generation;
                if(ShowNovelAuthorization(true))
                {
                    VideoPlatform.Invalidate();
                    if(generation==state.Generation && active())refresh(false);
                }
            },false);
            authorize.Visibility=Visibility.Collapsed;actions.Children.Add(authorize);
            refresh=async delegate(bool useReady)
            {
                var selection=targets();
                string key=signature(selection);
                if(useReady && key==state.Signature && state.Current!=null){accept();return;}
                if(useReady)panel.BringIntoView();
                long generation=state.Change(key);
                confirm.IsEnabled=false;
                retry.Visibility=authorize.Visibility=Visibility.Collapsed;
                if(quoteWork!=null){quoteWork.Cancel();quoteWork=null;}
                if(selection.Count==0){details.Text="请先勾选待生成片段。";return;}
                if(book.RatioMismatchNote!=null){details.Text=book.RatioMismatchNote;return;}
                var snapshot=choices(selection);
                var work=new CancellationTokenSource();quoteWork=panelWork=work;
                details.Text="正在重新询价 · "+selection.Count+" 段";
                try
                {
                    await Task.Delay(180,work.Token);
                    bool guide=useReady;
                    VideoQuote quote;
                    VideoPlatform liveCatalog=null;
                    while(true)
                    {
                        quote=await Task.Run(delegate
                        {
                            var current=Film.Open(root);
                            VideoDelivery.RequireAlignedRatio(root,current.Brief.Ratio);
                            if(current.Revision!=book.Revision)throw new InvalidDataException("定稿已变化，请刷新视频页。");
                            var cli=NovelVideo.Find();
                            var catalog=cli==null?VideoPlatform.OfflineCatalog:VideoPlatform.Load(work.Token);
                            liveCatalog=catalog;
                            if(!NovelAuth.Connected(cli,work.Token))return null;
                            var clips=VideoDelivery.PlanForSelections(root,current,cfg.Video,snapshot,catalog);
                            VideoQuote result;
                            try{result=new NovelVideo(root,work.Token).Prepare(current.Brief.Ratio,clips,delegate{});}
                            catch(IOException)
                            {
                                // The account can sign out between the status check and the estimate.
                                if(!NovelAuth.Connected(cli,work.Token))return null;
                                throw;
                            }
                            if(result.FilmRevision!=book.Revision)throw new InvalidDataException("定稿已变化，请刷新视频页。");
                            return result;
                        },work.Token);
                        if(!active() || work.IsCancellationRequested || !state.Matches(generation,key))return;
                        if(quote!=null)break;
                        details.Text="询价失败 · 请先完成即梦短剧账号授权。";
                        retry.Visibility=authorize.Visibility=Visibility.Visible;
                        if(!guide || !ShowNovelAuthorization(true))return;
                        VideoPlatform.Invalidate();
                        if(!active() || work.IsCancellationRequested || !state.Matches(generation,key))return;
                        guide=false;
                        retry.Visibility=authorize.Visibility=Visibility.Collapsed;
                        details.Text="授权成功，正在重新询价 · "+selection.Count+" 段";
                    }
                    if(liveCatalog!=null && !liveCatalog.Offline && (pickerCatalog==null || pickerCatalog.Offline))
                    {
                        pickerCatalog=liveCatalog;
                        if(refreshPickers!=null)refreshPickers();
                    }
                    if(!active() || work.IsCancellationRequested || !state.Receive(generation,key,quote))return;
                    details.Text=String.Join("\n",quote.Items.Select(i=>i.Title+" · "+i.Model+" · "+Quality(i.Resolution)+" · "+i.Credits+" 积分"))+
                        "\n合计 "+quote.Total+" 积分　余额 "+quote.Balance+"　预计剩余 "+quote.Remaining+(quote.Remaining<0?"（余额不足）":"");
                    confirm.IsEnabled=quote.Remaining>=0;
                    if(useReady && confirm.IsEnabled)accept();
                }
                catch(OperationCanceledException){}
                catch(Exception e)
                {
                    if(active() && state.Matches(generation,key))
                    {details.Text="询价失败 · "+e.GetBaseException().Message;retry.Visibility=Visibility.Visible;}
                }
                finally
                {
                    if(Object.ReferenceEquals(work,quoteWork))quoteWork=null;
                    if(Object.ReferenceEquals(work,panelWork))panelWork=null;
                    work.Dispose();
                }
            };
            revalidateQuote=delegate
            {
                if(state.Signature!=null && !active())refresh(false);
            };
            bool resumePrice=false;
            panel.Unloaded+=delegate
            {
                resumePrice=state.Signature!=null;
                if(panelWork!=null)panelWork.Cancel();
                state.Change(null);
                confirm.IsEnabled=false;
                details.Text="选择片段后取得本次报价。";
            };
            panel.Loaded+=delegate
            {
                if(resumePrice && page==pageRevision){resumePrice=false;refresh(false);}
            };
            request=refresh;
            return panel;
        }

        VideoClipChoice PickerChoice(string segment,VideoOptions defaults)
        {
            var choice=pickerDraft.Choice(segment,defaults);
            var model=pickerCatalog.Models.FirstOrDefault(m=>VideoPlatform.Canonical(m.Name)==VideoPlatform.Canonical(choice.Model));
            if(model==null || model.Resolutions.Contains(choice.Resolution))return choice;
            return new VideoClipChoice{Model=choice.Model,Resolution=model.Resolutions.Contains(model.DefaultResolution)?
                model.DefaultResolution:model.Resolutions.FirstOrDefault()};
        }

        FrameworkElement VideoPicker(string root,VideoOptions defaults,string segment,Button button,Action changed=null)
        {
            bool first=pickerPage!=pageRevision;
            if(first)
            {
                pickerPage=pageRevision;
                pickerCatalog=VideoPlatform.OfflineCatalog;
                pickerDraft=VideoSelectionDraft.Load(root,defaults);
                refreshPickers=null;
            }
            var group=new StackPanel{HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,2,6,2)};
            var bar=new StackPanel{Orientation=Orientation.Horizontal};
            var models=new ComboBox{Width=138,VerticalAlignment=VerticalAlignment.Center,ToolTip="即梦视频模型"};
            var sizes=new ComboBox{Width=154,VerticalAlignment=VerticalAlignment.Center,ToolTip="视频分辨率"};
            NameControl(models,"即梦视频模型");NameControl(sizes,"视频分辨率");
            bar.Children.Add(models);bar.Children.Add(sizes);bar.Children.Add(button);group.Children.Add(bar);
            bool rendering=false;
            Action draw=delegate
            {
                rendering=true;
                try
                {
                    var choice=PickerChoice(segment,defaults);
                    models.ItemsSource=pickerCatalog.Models;
                    var model=pickerCatalog.Models.FirstOrDefault(m=>VideoPlatform.Canonical(m.Name)==VideoPlatform.Canonical(choice.Model));
                    models.SelectedItem=model;
                    models.ToolTip=model==null?"目录中没有 "+choice.Model:model.ToString();
                    if(model==null){sizes.ItemsSource=null;sizes.IsEnabled=false;return;}
                    sizes.ItemsSource=model.Resolutions.Length==0?new[]{FixedQuality}:model.Resolutions;
                    sizes.SelectedItem=model.Resolutions.Length==0?FixedQuality:choice.Resolution;
                    sizes.IsEnabled=model.Resolutions.Length>1;
                }
                finally{rendering=false;}
            };
            Action<VideoModel,string> store=delegate(VideoModel model,string size)
            {
                var choice=new VideoClipChoice{Model=VideoPlatform.Canonical(model.Name),Resolution=size};
                try{pickerDraft=VideoSelectionDraft.Save(root,defaults,segment,choice);}
                finally{if(refreshPickers!=null)refreshPickers();}
                if(changed!=null)changed();
                else if(revalidateQuote!=null)revalidateQuote();
            };
            models.SelectionChanged+=delegate
            {
                var model=models.SelectedItem as VideoModel;
                if(rendering || model==null)return;
                var size=PickerChoice(segment,defaults).Resolution;
                if(!model.Resolutions.Contains(size))size=model.Resolutions.Contains(model.DefaultResolution)?model.DefaultResolution:model.Resolutions.FirstOrDefault();
                TryAction(()=>store(model,size));
            };
            sizes.SelectionChanged+=delegate
            {
                var model=models.SelectedItem as VideoModel;
                if(!rendering && sizes.SelectedItem!=null && model!=null)
                    TryAction(()=>store(model,model.Resolutions.Length==0?null:(string)sizes.SelectedItem));
            };
            refreshPickers+=draw;
            int ownerPage=pageRevision;
            group.Loaded+=delegate
            {
                if(pageRevision!=ownerPage)return;
                refreshPickers-=draw;refreshPickers+=draw;draw();
            };
            group.Unloaded+=delegate{if(pickerPage==ownerPage)refreshPickers-=draw;};
            draw();
            if(first)
            {
                int page=pageRevision;
                var note=Muted("",12,2);
                Action load=null;
                var retry=ActionButton("重试加载目录",()=>{VideoPlatform.Invalidate();load();},false);
                group.Children.Add(note);group.Children.Add(retry);
                load=async delegate
                {
                    retry.Visibility=Visibility.Collapsed;note.Visibility=Visibility.Visible;note.Text="正在更新模型目录";
                    try
                    {
                        var catalog=await Task.Run(()=>NovelVideo.Find()==null?VideoPlatform.OfflineCatalog:VideoPlatform.Load(CancellationToken.None));
                        if(page!=pageRevision)return;
                        pickerCatalog=catalog;
                        if(refreshPickers!=null)refreshPickers();
                        if(revalidateQuote!=null)revalidateQuote();
                        note.Text=catalog.Offline?"使用离线目录 · "+(catalog.LoadError??"本机未找到即梦短剧 CLI。"):"";
                        retry.Visibility=catalog.Offline?Visibility.Visible:Visibility.Collapsed;
                    }
                    catch(Exception e)
                    {
                        if(page!=pageRevision)return;
                        note.Text="使用离线目录 · "+e.GetBaseException().Message;
                        retry.Visibility=Visibility.Visible;
                    }
                    note.Visibility=note.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
                };
                load();
            }
            return group;
        }
    }
}
