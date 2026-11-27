using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private void ShowFeedbackCenter()
        {
            BeginPage(2,"问题反馈",null);
            AddPageBack("返回工作台",ShowWorkbench);
            var section=SystemSection("我的反馈");
            section.Children.Add(ActionButton("新建反馈",()=>EditFeedback(new FeedbackCase{Id=AgentData.Id(),Created=DateTimeOffset.Now.ToString("o"),
                Occurred=DateTimeOffset.Now.ToString("o"),Project=projectFolder,Stage="全部",Status="草稿"}),true));
            foreach(var item in Feedback.List())
            {
                var c=item;
                var row=SystemActions();
                row.Children.Add(ActionButton(AgentData.Short(c.Description??"新反馈",45),()=>EditFeedback(c),false));
                row.Children.Add(StudioText(DateTimeOffset.Parse(c.Created).ToLocalTime().ToString("MM-dd HH:mm")+" · "+(c.Status=="分析中"?"分析中或上次中断，可重试":c.Status),13,3));
                if(File.Exists(Feedback.Zip(c)))row.Children.Add(ActionButton("反馈报告",()=>OpenReport(c),false));
                section.Children.Add(row);
            }
        }
        private void OpenReport(FeedbackCase c)
        {
            TryAction(()=>{using(Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Feedback.Zip(c))){UseShellExecute=true})){}});
        }
        private void EditFeedback(FeedbackCase c)
        {
            Directory.CreateDirectory(Feedback.Folder(c));
            FileStream lease;
            try {lease=new FileStream(Feedback.Local(c,"编辑.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){SetStatus("此反馈正在另一个窗口中编辑。");return;}
            using(lease)
            {
                var dialog=new Window{Title="问题反馈 · Lacuna",Owner=Window,Width=790,Height=720,MinWidth=540,MinHeight=480,
                    WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Window.Background,Foreground=Window.Foreground};
                var stack=new StackPanel{Margin=new Thickness(20)};
                dialog.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
                stack.Children.Add(Title("描述遇到的问题",21));
                var body=AgentField(stack,"操作步骤、预期结果和实际现象",c.Description??"",92);
                var when=AgentField(stack,"发生时间（本地时间）",DateTimeOffset.Parse(c.Occurred).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                var stage=new ComboBox{ItemsSource=new[]{"全部","程序","剧本分析","资产生图","视频生成"},SelectedValue=c.Stage,MinHeight=32};
                stack.Children.Add(StudioText("发生环节 · "+(String.IsNullOrEmpty(c.Project)?"未打开项目":Path.GetFileName(c.Project)),13,5));
                stack.Children.Add(stage);
                var run=AgentField(stack,"运行编号（可选，用于缩小日志范围）",c.Run??"");
                stack.Children.Add(StudioText("检查截图和日志后开始分析。所选内容将发送至设置中选定的 Codex 账号或 Responses API。报告保存在本机，由你决定何时转发。",13,5));
                var buttons=SystemActions();stack.Children.Add(buttons);
                var preview=new StackPanel();stack.Children.Add(preview);
                var status=StudioText(c.Error??c.Status,14,6);stack.Children.Add(status);
                var actions=SystemActions();stack.Children.Add(actions);
                bool busy=false,changed=false,reviewed=c.Evidence.Any(e=>e.Image==null);
                Action dirty=()=>{changed=true;};
                body.TextChanged+=delegate{dirty();}; when.TextChanged+=delegate{dirty();}; stage.SelectionChanged+=delegate{dirty();};run.TextChanged+=delegate{dirty();};
                Action capture=()=>{
                    DateTimeOffset at;
                    if(!DateTimeOffset.TryParse(when.Text,out at))throw new InvalidDataException("请填写有效的发生时间。");
                    string chosen=(string)stage.SelectedValue??"全部";
                    if(c.Stage!=chosen || (c.Run??"")!=(run.Text??"") || Math.Abs((DateTimeOffset.Parse(c.Occurred)-at).TotalSeconds)>=1)
                        { c.Evidence.RemoveAll(e=>e.Image==null);reviewed=false; }
                    c.Description=body.Text.Trim();c.Occurred=at.ToString("o"); c.Stage=chosen;c.Run=run.Text??"";
                    if(changed){c.Finding=null;c.Error=null;c.Status="草稿";if(File.Exists(Feedback.Zip(c)))File.Delete(Feedback.Zip(c));changed=false;}
                    Feedback.Save(c);
                };
                Action draw=null;
                draw=()=>{
                    preview.Children.Clear();
                    foreach(var evidence in c.Evidence.ToArray())
                    {
                        var e=evidence;
                        var row=SystemActions();row.Children.Add(StudioText(e.Id+" · "+e.Source,13,2));
                        row.Children.Add(ActionButton("移除",()=>{c.Evidence.Remove(e);dirty();draw();},false));
                        preview.Children.Add(row);
                        if(e.Image!=null)
                        {
                            var bmp=new BitmapImage(); bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.DecodePixelWidth=640;
                            bmp.UriSource=new Uri(Feedback.ImagePath(c,e));bmp.EndInit();
                            preview.Children.Add(new Image{Source=bmp,MaxHeight=180,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left});
                        }
                        else preview.Children.Add(new Expander{Header="查看日志摘录",Content=new TextBox{Text=e.Text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap}});
                    }
                    if(c.Finding!=null)preview.Children.Add(StudioText(String.Join("\n",c.Finding.facts.Concat(c.Finding.questions).ToArray()),14,4));
                };
                Action<BitmapSource> add=bmp=>{
                    if(bmp==null)throw new InvalidDataException("剪贴板中没有图片。");
                    if(c.Evidence.Count(e=>e.Image!=null)>=10)throw new InvalidDataException("每条反馈最多附 10 张截图。");
                    if((long)bmp.PixelWidth*bmp.PixelHeight>24000000)throw new InvalidDataException("请缩小图片后添加。");
                    int n=1;while(c.Evidence.Any(e=>e.Id=="S"+n))n++;
                    var ev=new FeedbackEvidence{Id="S"+n,Image=AgentData.Id()+".png",Source="成员截图",Time=DateTimeOffset.Now.ToString("o")};
                    string p=Feedback.ImagePath(c,ev);Directory.CreateDirectory(Path.GetDirectoryName(p));
                    var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp,null,null,null));using(var fs=File.Create(p))enc.Save(fs);
                    c.Evidence.Add(ev);dirty();draw();
                };
                buttons.Children.Add(ActionButton("选择图片",()=>TryAction(()=>{
                    var file=new OpenFileDialog{Filter="截图|*.png;*.jpg;*.jpeg;*.bmp",Multiselect=true};
                    if(file.ShowDialog(dialog)!=true)return;
                    foreach(string path in file.FileNames){using(var fs=File.OpenRead(path))add(BitmapFrame.Create(fs,BitmapCreateOptions.None,BitmapCacheOption.OnLoad));}
                }),false));
                buttons.Children.Add(ActionButton("粘贴截图",()=>TryAction(()=>add(Clipboard.GetImage())),false));
                buttons.Children.Add(ActionButton("匹配并预览日志",()=>TryAction(()=>{
                    capture();var logs=Feedback.Collect(c);c.Evidence.RemoveAll(e=>e.Image==null);c.Evidence.AddRange(logs);
                    reviewed=true;dirty();capture();draw();status.Text="已匹配项目摘要与故障日志，共 "+logs.Count+" 份，可逐份查看和移除。";
                }),false));
                actions.Children.Add(ActionButton("开始分析",async ()=>{
                    if(busy || !TryAction(capture))return;
                    if(String.IsNullOrWhiteSpace(c.Description)){status.Text="请先描述遇到的问题。";return;}
                    if(!reviewed){bool ok=TryAction(()=>{var logs=Feedback.Collect(c);reviewed=true;if(logs.Count>0){c.Evidence.AddRange(logs);dirty();capture();draw();status.Text="已匹配日志，请检查后再点开始分析。";}});if(!ok || c.Evidence.Any(e=>e.Image==null))return;}
                    busy=true;stack.IsEnabled=false;
                    try
                    {
                        c.Status="分析中";c.Error=null;Feedback.Save(c);status.Text="正在使用选定的连接分析…";
                        string path=preferences.CodexPath;
                        c.Finding=await Task.Run(()=>Feedback.Analyze(c,path));
                        c.Status=c.Finding.questions.Length==0?"已分析":"待补充";
                        Feedback.Export(c);Feedback.Save(c);draw();status.Text=c.Status+" · 已生成反馈报告";
                    }
                    catch(Exception ex){c.Status="分析失败，可重试";c.Error=Feedback.Scrub(ex.GetBaseException().Message);Feedback.Save(c);status.Text=c.Error;}
                    finally{busy=false;stack.IsEnabled=true;}
                },true));
                actions.Children.Add(ActionButton("保存草稿",()=>TryAction(()=>{capture();status.Text="已保存";}),false));
                actions.Children.Add(ActionButton("导出报告",()=>TryAction(()=>{
                    capture();if(String.IsNullOrWhiteSpace(c.Description))throw new IOException("请先描述遇到的问题。");
                    if(c.Finding==null)c.Status="基础报告";Feedback.Export(c);Feedback.Save(c);OpenReport(c);
                }),false));
                draw();dialog.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(busy){e.Cancel=true;return;}
                    if(changed && !TryAction(capture))e.Cancel=true;};
                dialog.ShowDialog();
            }
            ShowFeedbackCenter();
        }
    }
}

