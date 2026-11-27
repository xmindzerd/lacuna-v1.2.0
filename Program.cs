using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Lacuna")]
[assembly: AssemblyProduct("Lacuna")]
[assembly: AssemblyDescription("短剧制作工作台")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("V1.2.0")]

namespace OneClickFilm
{
    internal static class Program
    {
        internal const string Version = "V1.2.0";

        internal static T Presentation<T>(string name)
        {
            using (Stream source = typeof(Program).Assembly.GetManifestResourceStream(name))
                return (T)XamlReader.Load(source);
        }

        internal static BitmapImage Artwork(string name)
        {
            using (Stream source = typeof(Program).Assembly.GetManifestResourceStream(name))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = source;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        internal static void LogFault(string source, Exception error)
        {
            if (error == null) return;
            try
            {
                string folder = SystemData.Active.Folder;
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "运行故障日志.log");
                string entry = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + Version + "] [" + source + "] " + error + "\r\n\r\n";
                File.AppendAllText(file, entry, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string FindPreviousSettings(string settingsFolder, string currentFile)
        {
            string first = Path.Combine(settingsFolder, "窗口设置-V1.1.6.json");
            string older = Path.Combine(settingsFolder, "窗口设置-V1.1.55.json");
            return File.Exists(first) || !File.Exists(older) ? first : older;
        }

        internal static void InheritSettings(string currentSettings, string previousSettings)
        {
            if (File.Exists(currentSettings) || !File.Exists(previousSettings)) return;
            string copy = currentSettings + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(previousSettings, copy);
                if (AgentData.Read<LocalPreferences>(copy) == null) throw new InvalidDataException("设置为空");
                try { File.Move(copy, currentSettings); }
                catch (IOException) { if (!File.Exists(currentSettings)) throw; }
            }
            finally { if (File.Exists(copy)) File.Delete(copy); }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            CodexAuthMode.Initialize();
            PdfScript.RegisterLibraries();
            if (args.Length > 0 && args[0] == "--agent-mcp")
            {
                AgentMcp.UseParentPipes();
                AgentMcp.Serve(Console.In, Console.Out, args);
                return;
            }


            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogFault("AppDomain", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += delegate(object sender, UnobservedTaskExceptionEventArgs e)
            {
                LogFault("TaskScheduler", e.Exception);
                e.SetObserved();
            };

            var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                LogFault("Dispatcher", e.Exception);
                if (e.Exception is OutOfMemoryException || e.Exception is StackOverflowException) return;
                e.Handled = true;
                try
                {
                    MessageBox.Show("操作遇到意外错误，已记录到运行故障日志：\n" + AgentData.Short(e.Exception.GetBaseException().Message, 320),
                        "Lacuna · " + Version, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            };

            app.Resources.MergedDictionaries.Add(Presentation<ResourceDictionary>("UiTheme.xaml"));
            var location = SystemData.Active;
            if (location.Missing && !SystemData.FindMissingLocation(location))
                return;
            string settingsFolder = SystemData.Active.Folder;
            string currentSettings = Path.Combine(settingsFolder, "窗口设置-" + Version + ".json");
            string previousSettings = FindPreviousSettings(settingsFolder, currentSettings);
            try { InheritSettings(currentSettings, previousSettings); }
            catch (Exception e)
            {
                MessageBox.Show("无法继承旧设置，请检查文件后重试。\n" + previousSettings + "\n" + e.Message, "Lacuna");
                return;
            }
            var store = new ProjectStore(currentSettings, previousSettings);
            var workspace = new Workspace(Presentation<Window>("Workspace.xaml"), store);
            try { app.Run(workspace.Window); } finally { CodexAppServer.Shutdown(); }
        }
    }
}
