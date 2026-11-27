using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.ComponentModel;
using Microsoft.Win32;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private CodexStatus codexStatus;
        private bool checkingCodex;
        private TextBlock codexDetails;
        private Button codexRefresh;
        private StackPanel codexModelBox;
        private TextBlock flowAccount;
        private bool refreshingAccount;
        private Button codexSelect;
        private Button codexAutomatic;
        private Button stopInstall;
        private bool sendingCodexTask;
        private bool exitAfterCodexTask;

        private void AddCodexSettings()
        {
            var section = new StackPanel { Margin = new Thickness(16) };
            section.Children.Add(Title("本机 Codex", 19));
            codexDetails = Muted("", 14, 8);
            codexDetails.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(codexDetails);

            var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            codexRefresh = ActionButton("重新检测", RefreshCodex, true);
            codexSelect = ActionButton("手动选择程序", SelectCodex, false);
            codexAutomatic = ActionButton("恢复自动查找", delegate { SetCodexPath(null); }, false);
            actions.Children.Add(codexRefresh);
            if (!CodexAuthMode.IsApi) actions.Children.Add(codexSelect);
            if (!CodexAuthMode.IsApi) actions.Children.Add(codexAutomatic);
            if (!CodexAuthMode.IsApi) actions.Children.Add(ActionButton("打开 Codex 登录", LoginCodex, false));
            if (!CodexAuthMode.IsApi) actions.Children.Add(ActionButton("安装 Codex CLI", InstallCodex, false));
            stopInstall = ActionButton("停止安装", delegate { if (installCancel != null) installCancel.Cancel(); }, false);
            actions.Children.Add(stopInstall);
            section.Children.Add(actions);
            installStatus = Muted(installState, 12, 6);
            installStatus.TextWrapping = TextWrapping.Wrap;
            section.Children.Add(installStatus);
            var panel = Panel(section);
            panel.Margin = new Thickness(0, 18, 0, 0);
            content.Children.Add(panel);
            var tools = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            tools.Children.Add(ActionButton("打开 Codex 助手", OpenAgent, true));
            tools.Children.Add(ActionButton("制作渠道", ShowAgentRoutes, false));
            tools.Children.Add(ActionButton("连接其它助手", ShowBridgeHelp, false));
            content.Children.Add(tools);
            codexModelBox=new StackPanel(); section.Children.Add(codexModelBox);
            AddModelPicker(codexModelBox);
            UpdateCodexDisplay();
        }

        private TextBox CodexTextBox(string name, string text, double minHeight)
        {
            var box = new TextBox
            {
                Text = text, MinHeight = minHeight,
                Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetName(box, name);
            return box;
        }

        private async void RefreshCodex()
        {
            if (checkingCodex || sendingCodexTask)
                return;
            checkingCodex = true;
            UpdateCodexDisplay();
            string selected = preferences.CodexPath;
            try
            {
                codexStatus = await Task.Run(() => {
                    var status=CodexConnection.Check(selected);
                    CodexAppServer.RefreshAccount(status.State==CodexState.SignedIn?status.Path:null);
                    return status;
                });
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException ||
                    error is ArgumentException || error is System.Security.SecurityException ||
                    error is AggregateException))
                    throw;
                codexStatus = new CodexStatus(CodexState.Unavailable, selected, null,
                    "无法完成检测。请检查程序位置后重试。");
            }
            finally
            {
                checkingCodex = false;
                RefreshModelChoices();
                UpdateCodexDisplay();
            }
        }

        private void RefreshModelChoices()
        {
            if(codexModelBox==null || !codexModelBox.IsLoaded)return;
            codexModelBox.Children.Clear();AddModelPicker(codexModelBox);
        }

        private void StartQuotaUpdates()
        {
            var ticker=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMinutes(2)};
            ticker.Tick+=async delegate
            {
                if(CodexAuthMode.IsApi || refreshingAccount || checkingCodex || codexStatus==null || codexStatus.State!=CodexState.SignedIn)return;
                refreshingAccount=true;
                try { var path=codexStatus.Path;await Task.Run(()=>CodexAppServer.RefreshAccount(path));UpdateCodexDisplay(); }
                finally {refreshingAccount=false;}
            };
            Window.Closed+=delegate {ticker.Stop();};ticker.Start();
        }

        private void UpdateCodexDisplay()
        {
            if (!Window.Dispatcher.CheckAccess())
            {
                if (!Window.Dispatcher.HasShutdownStarted)
                    Window.Dispatcher.BeginInvoke(new Action(UpdateCodexDisplay));
                return;
            }
            string label = sendingCodexTask ? "任务进行中" :
                (checkingCodex ? "检测中" : (codexStatus == null ? "待检测" : codexStatus.Label));
            var shortcut = Find<Button>("CodexShortcut");
            var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string dotBrush = sendingCodexTask || checkingCodex ? "Review" :
                (codexStatus != null && codexStatus.State == CodexState.SignedIn ? "Accent" :
                 codexStatus != null && codexStatus.State == CodexState.SignedOut ? "Review" : "TextMuted");
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = Brush(dotBrush),
                Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center
            };
            UiFx.SetPulse(dot, sendingCodexTask || checkingCodex, Sys);
            chip.Children.Add(dot);
            string limits=CodexAppServer.CompactQuota;
            string account=CodexAppServer.Summary(agentOptions);
            chip.Children.Add(Text(CodexAuthMode.Label + " · " + label+" · "+limits, 12, 0));
            shortcut.ToolTip=account;
            if(flowAccount!=null)flowAccount.Text=account;
            AutomationProperties.SetHelpText(shortcut,account);
            shortcut.Content = chip;
            AutomationProperties.SetName(shortcut, CodexAuthMode.Label + " · " + label);

            if (stopInstall != null) stopInstall.Visibility = installCancel == null ? Visibility.Collapsed : Visibility.Visible;
            if (codexDetails == null)
                return;
            string mode = CodexAuthMode.IsApi ? "OpenAI Responses API 直连" : String.IsNullOrEmpty(preferences.CodexPath) ? "自动查找" : "手动选择";
            codexDetails.Text = label + "  ·  " + mode;
            if (!checkingCodex && codexStatus != null)
            {
                if (codexStatus.Version != null)
                    codexDetails.Text += "  ·  版本 " + codexStatus.Version;
                codexDetails.Text += "\n" + codexStatus.Detail+"\n"+account;
                if (codexStatus.Path != null)
                    codexDetails.Text += "\n" + codexStatus.Path;
            }
            codexRefresh.IsEnabled = codexSelect.IsEnabled = !checkingCodex && !sendingCodexTask;
            codexAutomatic.Visibility = String.IsNullOrEmpty(preferences.CodexPath) ? Visibility.Collapsed : Visibility.Visible;
            codexAutomatic.IsEnabled = !checkingCodex && !sendingCodexTask;
            UpdateAgentUi();
        }

        private void SelectCodex()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择本机 Codex CLI",
                Filter = "Codex CLI|codex.exe;codex.cmd",
                CheckFileExists = true,
                DereferenceLinks = true
            };
            if (dialog.ShowDialog(Window) != true)
                return;
            string file = Path.GetFileName(dialog.FileName);
            if (!String.Equals(file, "codex.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(file, "codex.cmd", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("请选择 codex.exe 或 codex.cmd。");
                return;
            }
            SetCodexPath(dialog.FileName);
        }

        private void SetCodexPath(string path)
        {
            if (TryAction(delegate
            {
                var updated = preferences.Copy();
                updated.CodexPath = path;
                store.SavePreferences(updated);
                DevMonitor.Action(projectFolder,"已保存 Codex 程序设置");
                preferences = updated;
            }))
                RefreshCodex();
        }
    }
}
