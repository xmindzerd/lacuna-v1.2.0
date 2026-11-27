using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        static string VideoSummary(VideoOptions o)
        {
            return (o!=null && o.Enabled ? "即梦 · "+VideoPlatform.DisplayName(o.Model)+" · "+(String.IsNullOrWhiteSpace(o.Resolution)?"默认画质":o.Resolution) : "只生成提示词")+
                (o!=null && o.Review ? " · 提示词人工审核" : "");
        }

        bool HasVideoStage()
        {
            if(projectFolder==null)return false;
            var cfg=FlowCreation.Config(projectFolder);
            return cfg!=null && cfg.Video!=null && cfg.Video.Enabled;
        }

        void AddVideoSettings(StackPanel host, FlowDraft draft, Button saveButton=null)
        {
            var opts=draft.Video ?? (draft.Video=new VideoOptions());
            var review=new CheckBox{Content="最终提示词完成后暂停，等我审核",IsChecked=opts.Review,Foreground=Window.Foreground};
            review.Checked+=delegate{opts.Review=true;}; review.Unchecked+=delegate{opts.Review=false;}; host.Children.Add(review);
            host.Children.Add(Muted("最终视频平台",14,12));
            var choice=new ComboBox{MinHeight=35};NameControl(choice,"最终视频平台");
            choice.Items.Add("只生成提示词");bool installed=NovelVideo.Find()!=null;
            if(installed || opts.Enabled)choice.Items.Add(installed ? "即梦 CLI · 视频生成" : "即梦 CLI · 本机未安装");
            host.Children.Add(choice);
            var fields=new StackPanel();host.Children.Add(fields);
            var note=StudioText("",13,4);host.Children.Add(note);
            int generation=0;
            Action<bool> canSave=ready=>{if(saveButton!=null)saveButton.IsEnabled=ready;};
            Action reload=null;
            reload=async delegate{
                int current=++generation;canSave(false);fields.Children.Clear();
                if(choice.SelectedIndex!=1){canSave(true);opts.Platform="prompt";note.Text=installed ? "" : "本机未检测到即梦 CLI。";return;}
                opts.Platform=FlowChannels.Dreamina; note.Text="正在读取即梦 CLI 可用模型…";
                try{
                    var catalog=await Task.Run(()=>VideoPlatform.Load(CancellationToken.None));
                    if(generation!=current)return;
                    var models=new ComboBox{ItemsSource=catalog.Models,MinHeight=32};
                    NameControl(models,"即梦视频模型");fields.Children.Add(Muted("视频模型",14,6));fields.Children.Add(models);
                    fields.Children.Add(ActionButton("刷新模型目录",()=>{VideoPlatform.Invalidate();reload();},false));
                    var sizes=new ComboBox{MinHeight=32};NameControl(sizes,"视频分辨率");
                    fields.Children.Add(Muted("分辨率",14,6));fields.Children.Add(sizes);
                    sizes.SelectionChanged+=delegate{if(sizes.IsEnabled && sizes.SelectedItem!=null)opts.Resolution=(string)sizes.SelectedItem;};
                    models.SelectionChanged+=delegate{
                        if(models.SelectedIndex<0)return;
                        var m=catalog.Models[models.SelectedIndex];opts.Model=VideoPlatform.Canonical(m.Name);
                        var preferred=opts.Resolution;
                        sizes.IsEnabled=m.Resolutions.Length!=0;
                        if(sizes.IsEnabled){
                            sizes.ItemsSource=m.Resolutions;
                            sizes.SelectedItem=m.Resolutions.Contains(preferred)?preferred:m.Resolutions.Contains(m.DefaultResolution)?m.DefaultResolution:m.Resolutions[0];
                        }else{
                            opts.Resolution=null;
                            sizes.ItemsSource=new[]{"模型默认（样片固定画质）"}; sizes.SelectedIndex=0;
                        }
                        bool supports=m.Ratios.Contains(draft.Ratio);
                        canSave(supports);
                        note.Text=(supports?"":"当前模型不支持项目画幅 "+draft.Ratio+"。\n")+ (catalog.Offline?"离线兜底目录，授权即梦后自动同步最新模型。\n":"")+
                            "项目画幅 "+draft.Ratio+" · 每段 "+m.Min+"–"+m.Max+" 秒 · 最多 "+m.Images+" 张参考图"+
                            (String.IsNullOrWhiteSpace(m.Price)?"":" · "+m.Price);
                    };
                    models.SelectedItem=catalog.Models.FirstOrDefault(m=>VideoPlatform.Canonical(m.Name)==VideoPlatform.Canonical(opts.Model)) ??
                        catalog.Models.FirstOrDefault(m=>VideoPlatform.Canonical(m.Name)=="Seedance_2.5") ?? catalog.Models[0];
                }catch(Exception e){if(current==generation)note.Text=e.GetBaseException().Message;}
            };
            choice.SelectionChanged+=delegate{reload();};
            choice.SelectedIndex=opts.Enabled?1:0;
        }

        void PromptReviewBar(string root,StackPanel host)
        {
            var cfg=FlowCreation.Config(root); if(cfg==null)return; var state=FlowCreation.State(root);
            if(AssetRunning(root,FlowTaskName))return;
            string key=state.PendingKey;
            if(FlowCreation.IsRegularOutput(root) && state.Status!="待确认费用" && state.Status!="待人工审核")
                host.Children.Add(ActionButton("交接本轮定稿",()=>StartFlow(root,null),true));
            var actions = new WrapPanel();
            if(state.Status=="待人工审核" && state.Phase=="提示词审核")
                actions.Children.Add(ActionButton("通过全部提示词审核",()=>StartFlow(root,"review/"+key),true));
            if (actions.Children.Count > 0) host.Children.Add(actions);
            if(cfg.Video!=null && cfg.Video.Enabled)
                host.Children.Add(VideoPicker(root,cfg.Video,null,ActionButton("批量或一键生成视频",ShowVideoJobs,false)));
        }

        void ResolveVideoReceipt(string root,ClipReceipt r)
        {
            var box=StyleDialog(new Window{Title="核对即梦任务",Owner=Window,Width=560,SizeToContent=SizeToContent.Height,
                MaxHeight=SystemParameters.WorkArea.Height-48,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("Panel"),ShowInTaskbar=false});
            var p=new StackPanel{Margin=new Thickness(22)};
            box.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            p.Children.Add(Title(r.Title,18));
            var field=new TextBox();
            if(r.Provider=="novel"){
                string detail=String.Join("\n",new[]{
                    String.IsNullOrWhiteSpace(r.Node)?null:"即梦节点 · "+r.Node,
                    String.IsNullOrWhiteSpace(r.ExternalId)?null:"平台任务号 · "+r.ExternalId,
                    String.IsNullOrWhiteSpace(r.SubmitError)?null:r.SubmitError,
                    String.IsNullOrWhiteSpace(r.Error)?null:r.Error
                }.Where(s=>s!=null));
                p.Children.Add(StudioText(detail.Length==0?"请在即梦短剧画布核实该节点状态。":detail,14,6));
            }
            else field=AgentField(p,"在即梦任务记录中找到的原任务号",r.ExternalId??"");
            bool reset=false;
            var actions=new WrapPanel{Margin=new Thickness(0,14,0,0)};
            if(r.Provider!="novel")actions.Children.Add(ActionButton("绑定原任务号，继续查询",()=>{if(VideoDelivery.ValidId(field.Text.Trim())) box.DialogResult=true;},false));
            if(r.Provider=="novel" && r.Status!="平台失败")
                p.Children.Add(StudioText("请返回视频生成页，点击「查询平台任务状态」核实原任务。",14,6));
            if(r.Provider!="novel" || r.Status=="平台失败")
                actions.Children.Add(ActionButton("已核实原任务失败或未提交，重新核价",()=>{reset=true;box.DialogResult=true;},false));
            actions.Children.Add(ActionButton("返回",()=>{box.DialogResult=false;},false));
            p.Children.Add(actions);
            if(box.ShowDialog()!=true)return;
            using(FlowCreation.Lock(root))
            {
                string path=VideoDelivery.ReceiptPath(root,r.Key);var latest=AgentData.Read<ClipReceipt>(path);
                if(latest==null || latest.Utc!=r.Utc)throw new InvalidOperationException("任务已有更新，请刷新。");
                var state=FlowCreation.State(root);
                if(reset){
                    if(latest.Provider=="novel" && latest.Status!="平台失败")throw new InvalidOperationException("请先查询平台任务状态。");
                    state.ChargeKey=null;state.ConfirmedCredits=null;state.Status="待确认费用";FlowCreation.Save(root,state);
                    File.Move(path,Path.Combine(Path.GetDirectoryName(path),"原任务-"+Guid.NewGuid().ToString("N")+".json"));
                }else{latest.ExternalId=field.Text.Trim();latest.Status="已提交";latest.Error=null;VideoDelivery.Save(path,latest);}
            }
            ShowVideoJobs();
        }

        FrameworkElement ReferenceTile(string root,PromptReference r,bool compact,Action focus)
        {
            var row=new StackPanel{Orientation=compact?Orientation.Horizontal:Orientation.Vertical};
            var picture=new Border{Width=compact?24:90,Height=compact?24:64,ClipToBounds=true,Child=AssetThumbnail(root,"",new AssetAttempt{Path=r.Path})};
            row.Children.Add(picture);
            var label=StudioText(compact?r.Label:r.Label+"\n"+r.Name,compact?12:13,0);
            label.MaxWidth=compact?180:106;label.TextTrimming=TextTrimming.CharacterEllipsis;row.Children.Add(label);
            var button=ActionButton(row,focus,false);button.ToolTip=r.Caption;button.Padding=new Thickness(3);
            button.Margin=new Thickness(2);NameControl(button,r.Caption);return button;
        }

        void PresentPrompt(StackPanel panel,string root,FilmPrompt prompt,string displayText,List<PromptReference> refs,Dictionary<string,FrameworkElement> cards)
        {
            refs=refs??new List<PromptReference>();
            var map=refs.GroupBy(r=>r.Label).ToDictionary(g=>g.Key,g=>g.First());
            Action<PromptReference> focus=r=>{FrameworkElement card;if(cards.TryGetValue(r.Label,out card))card.BringIntoView();};
            var strip=new WrapPanel();
            foreach(var r in map.Values)strip.Children.Add(ReferenceTile(root,r,false,()=>focus(r)));
            if(map.Count>0)panel.Children.Add(strip);
            var text=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=15,Foreground=Brush("TextMain"),LineHeight=25,Margin=new Thickness(12)};
            int start=0;
            foreach(Match m in Regex.Matches(displayText,@"@Image\d+"))
            {
                PromptReference r;if(!map.TryGetValue(m.Value,out r))continue;
                text.Inlines.Add(new Run(displayText.Substring(start,m.Index-start)));
                text.Inlines.Add(new InlineUIContainer(ReferenceTile(root,r,true,()=>focus(r))){BaselineAlignment=BaselineAlignment.Center});
                start=m.Index+m.Length;
            }
            text.Inlines.Add(new Run(displayText.Substring(start)));
            panel.Children.Add(new Border{Background=Brush("Input"),CornerRadius=new CornerRadius(6),Child=text,Margin=new Thickness(0,8,0,8)});
            var original=new TextBox{Text=displayText,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=80,MaxHeight=360,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            if (prompt.SkillFaithful)
            {
                try
                {
                    string bound = VideoPromptFidelity.BindForSubmission(prompt,refs);
                    if (bound != displayText)
                        panel.Children.Add(new Expander { Header = "查看提交副本的参考图绑定", Content = new TextBox {
                            Text = bound, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 360,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Foreground = Window.Foreground });
                }
                catch (InvalidDataException issue) { panel.Children.Add(Notice(issue.Message)); }
            }
            panel.Children.Add(new Expander{Header="查看可复制的提示词原文",Content=original,Foreground=Window.Foreground});
        }
    }
}
