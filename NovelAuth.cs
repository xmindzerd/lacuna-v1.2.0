using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Row=System.Collections.Generic.Dictionary<string,object>;

namespace OneClickFilm
{
    internal static class NovelAuth
    {
        static object Value(Row row,string key){return AgentData.Value(row,key);}
        static Row Child(Row row,string key){return Value(row,key) as Row;}
        static string Text(Row row,string key){return Convert.ToString(Value(row,key));}
        static bool True(Row row,string key){return Object.Equals(Value(row,key),true);}

        static Row Request(string exe,string[] args,int timeout,CancellationToken token)
        {
            if(String.IsNullOrEmpty(exe))throw new FileNotFoundException("未找到即梦短剧 CLI。");
            token.ThrowIfCancellationRequested();
            var r=CodexConnection.Run(exe,AgentRunner.Args(args),timeout,Path.GetDirectoryName(exe),cancellation:token,useSystemProxy:true);
            try{
                var result=AgentData.Json().Deserialize<Row>(r.Output);
                if(result!=null && (r.Code==0 || Child(result,"error")!=null))return result;
            }catch(ArgumentException){}catch(InvalidOperationException){}
            throw new IOException("即梦没有返回可识别的授权结果，请检查网络后重试。");
        }
        static string Reason(Row reply){
            var err=Child(reply,"error");var auth=Child(Child(err,"details"),"auth");
            string code=Text(auth,"reason");
            if(String.IsNullOrEmpty(code))code=Text(auth,"code");
            if(String.IsNullOrEmpty(code))code=Text(err,"code");
            return code;
        }
        static IOException Failure(Row reply){
            string reason=Reason(reply);
            string note="请稍后重新检查即梦授权。";
            if(reason.Contains("network") || reason.Contains("dns"))note="即梦网络连接失败，请检查网络或系统代理后重试。";
            if(reason.Contains("credential_store"))note="即梦无法读取或保存本机登录，请检查当前 Windows 用户的凭据存储。";
            if(reason.Contains("denied") || reason.Contains("cancel"))note="即梦授权未完成。"+note;
            return new IOException(note+(System.Text.RegularExpressions.Regex.IsMatch(reason,@"^[A-Za-z0-9_]{1,64}$")?"（"+reason+"）":""));
        }
        internal static bool Connected(string exe,CancellationToken token)
        {
            var r=Request(exe,new[]{"status","-j"},20000,token);
            if(True(r,"logged_in") && Text(r,"status")=="logged_in")return true;
            if(Object.Equals(Value(r,"logged_in"),false))return false;
            if(Text(r,"status")=="not_logged_in" || Child(r,"data")!=null && Text(Child(r,"data"),"status")=="authorization_required")return false;
            throw Failure(r);
        }
        static bool Verified(Row data){
            string status=Text(data,"status");
            return (status=="reused" || status=="logged_in") && True(Child(data,"state"),"loggedIn");
        }
        internal static void Connect(string exe,Action<Uri,string> show,CancellationToken token)
        {
            using(var gate=new Mutex(false,@"Local\Lacuna.JimengNovel.Login")){
                bool acquired=false;
                try{
                    try{acquired=gate.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
                    if(!acquired)throw new IOException("另一个 Lacuna 窗口正在等待即梦授权，请先完成该窗口的授权。");
                    for(int round=0;round<2;round++){
                        var args=new List<string>{"login","--non-interactive","--timeout","25s","-j"};
                        if(round>0)args.Add("--restart");
                        var first=Request(exe,args.ToArray(),32000,token);var data=Child(first,"data");
                        if(Verified(data))return;
                        if(Text(data,"status")!="authorization_required")throw Failure(first);
                        var challenge=Child(data,"challenge");var device=Text(challenge,"deviceCode");
                        string link=Text(challenge,"verificationUriComplete"),code="";
                        if(String.IsNullOrWhiteSpace(link)){link=Text(challenge,"verificationUri");code=Text(challenge,"userCode");}
                        Uri uri;
                        if(String.IsNullOrWhiteSpace(device) || !Uri.TryCreate(link,UriKind.Absolute,out uri) ||
                            uri.Scheme!="https" || !uri.IsDefaultPort || uri.UserInfo.Length!=0 || uri.IdnHost!="jimeng.jianying.com")
                            throw new IOException("即梦没有返回有效的官方授权链接。");
                        token.ThrowIfCancellationRequested();show(uri,code);
                        var wait=Request(exe,new[]{"login","--wait","--device-code",device,"--timeout","10m","-j"},615000,token);
                        if(Verified(Child(wait,"data")))return;
                        string reason=Reason(wait);
                        var auth=Child(Child(Child(wait,"error"),"details"),"auth");
                        if(round==0 && (Text(auth,"upstreamCode")=="40014" || reason=="authorization_expired"))continue;
                        if(reason.Contains("timeout") || Text(auth,"upstreamCode")=="40011")
                            throw new IOException("等待授权超时。关闭后再点「即梦短剧账号授权」可以继续。");
                        throw Failure(wait);
                    }
                }finally{if(acquired)gate.ReleaseMutex();}
            }
        }
    }

    internal sealed partial class Workspace
    {
        bool ShowNovelAuthorization(bool continueWhenReady=false)
        {
            var box=StyleDialog(new Window{Owner=Window,Title="即梦账号授权",Width=540,SizeToContent=SizeToContent.Height,
                MaxHeight=SystemParameters.WorkArea.Height-48,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("Panel"),ShowInTaskbar=false});
            var p=new StackPanel{Margin=new Thickness(24)};
            box.Content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            p.Children.Add(Title("连接即梦短剧账号",20));
            p.Children.Add(StudioText("即梦用此账号查询积分、为所选片段报价。提交视频前还会单独确认费用。",14,6));
            var note=StudioText("正在检查本机即梦登录…",14.5,10);p.Children.Add(note);
            var actions=new WrapPanel{Margin=new Thickness(0,14,0,0)};p.Children.Add(actions);Uri link=null;
            var open=ActionButton("打开授权页",()=>TryAction(()=>{ if(link!=null) using(Process.Start(new ProcessStartInfo(link.AbsoluteUri){UseShellExecute=true})){} }),true);
            var copy=ActionButton("复制授权链接",()=>{ if(link!=null) CopyText(link.AbsoluteUri,"已复制即梦授权链接"); },false);
            actions.Children.Add(open);actions.Children.Add(copy);open.Visibility=copy.Visibility=Visibility.Collapsed;
            bool connected=false,closed=false,finished=false;
            actions.Children.Add(ActionButton("关闭",()=>box.Close(),false));
            var cancel=new CancellationTokenSource();
            box.Closed+=delegate{closed=true;UiFx.SafeCancel(cancel);if(finished)cancel.Dispose();};
            box.Loaded+=async delegate{
                try{
                    await Task.Run(()=>NovelAuth.Connect(NovelVideo.Find(),(uri,code)=>box.Dispatcher.Invoke(new Action(()=>{
                        if(closed)return;
                        link=uri;open.Visibility=copy.Visibility=Visibility.Visible;
                        string extra=code.Length>0?" 网页验证码 "+code:"";
                        note.Text="请在即梦网页确认，软件正在等待登录结果。"+extra;
                        try{using(Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true})){}}
                        catch(System.ComponentModel.Win32Exception){note.Text="无法启动默认浏览器，请复制授权链接，在浏览器中打开。"+extra;}
                    })),cancel.Token));
                    if(closed)return;
                    VideoPlatform.Invalidate();
                    connected=true;open.Visibility=copy.Visibility=Visibility.Collapsed;
                    note.Text="已连接即梦账号，可以直接选择片段生成。";SetStatus("即梦短剧账号已连接");
                    if(continueWhenReady)box.Close();
                }catch(OperationCanceledException){}catch(Exception e){
                    if(!closed){note.Text=e.GetBaseException().Message;open.Visibility=copy.Visibility=Visibility.Collapsed;}
                }finally{finished=true;if(closed)cancel.Dispose();}
            };
            box.ShowDialog();
            return connected;
        }
    }
}
