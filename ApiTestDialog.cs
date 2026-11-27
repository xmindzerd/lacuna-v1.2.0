using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private Action closeKeyEditor;
        private bool apiTesting;

        private CodexApiModels TestApiConnection(Uri endpoint, string key, out string outcome)
        {
            var dialog = StyleDialog(new Window {
                Title = "正在测试连接至 API", Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 390, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
            });
            var panel = new StackPanel { Margin = new Thickness(20) };
            dialog.Content = panel;
            panel.Children.Add(Title("正在读取此项目的模型列表", 16));
            panel.Children.Add(new ProgressBar { Height=6, IsIndeterminate=true, Margin=new Thickness(0,14,0,8) });
            var time = Muted("最多 15 秒 · 剩余 15 秒",13,4);
            panel.Children.Add(time);
            var stop = new Button { Content = "取消测试", IsCancel = true, Margin=new Thickness(0,12,0,0) };
            panel.Children.Add(stop);
            var cancel = new CancellationTokenSource();
            var token = cancel.Token;
            var clock = new Stopwatch();
            var tick = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
            CodexApiModels result = null;
            string message = "已取消";
            bool ended = false, started = false;
            Action<string> finish = value => {
                if (ended) return;
                ended = true;
                message = CodexAuthMode.RedactTestMessage(value,key);
                tick.Stop();
                dialog.Close();
            };
            stop.Click+=delegate { finish("已取消"); };
            dialog.PreviewKeyDown+=(sender,e)=>{
                if(e.Key==System.Windows.Input.Key.Escape){e.Handled=true;finish("已取消");}
            };
            tick.Tick += delegate {
                int left = Math.Max(0,15-(int)clock.Elapsed.TotalSeconds);
                time.Text = "最多 15 秒 · 剩余 "+left+" 秒";
                if (left == 0) finish("15 秒内未完成");
            };
            bool ownerEnabled = Window.IsEnabled;
            dialog.Closing += delegate { Window.IsEnabled=ownerEnabled; ended=true; tick.Stop(); cancel.Cancel(); };
            dialog.ContentRendered += async delegate {
                if (started || ended) return;
                started=true; clock.Start(); tick.Start();
                try {
                    var reply = await Task.Run(() => CodexAuthMode.RequestModels(endpoint,key,clock,token));
                    if (!ended && clock.ElapsedMilliseconds < 15000) { result=reply; finish(reply.Describe("")); }
                    else if (!ended) finish("15 秒内未完成");
                }
                catch(Exception e) { if (!ended) finish(clock.ElapsedMilliseconds>=15000?"15 秒内未完成":e.GetBaseException().Message); }
                finally { cancel.Dispose(); }
            };
            var assistant = agentWindow;
            bool assistantEnabled = assistant != null && assistant.IsEnabled;
            try {
                shell.apiTesting=true;
                Window.IsEnabled=false;
                if (assistant!=null) assistant.IsEnabled=false;
                dialog.ShowDialog();
            }
            finally {
                shell.apiTesting=false;
                tick.Stop(); Window.IsEnabled=ownerEnabled;
                if(assistant!=null) assistant.IsEnabled=assistantEnabled;
                if(!started) cancel.Dispose();
            }
            if(FlowCreation.Development)DevMonitor.Log(projectFolder,"UI","API GET /models · "+clock.ElapsedMilliseconds+" ms · "+message);
            outcome = message;
            return result;
        }
    }
}
