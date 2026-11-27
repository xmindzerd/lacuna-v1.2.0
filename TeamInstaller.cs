using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly:AssemblyTitle("升级一键成片")]
[assembly:AssemblyProduct("Lacuna Team Installer")]
[assembly:AssemblyFileVersion("1.2.0.0")]
[assembly:AssemblyVersion("1.2.0.0")]
class TeamInstaller
{
    public sealed class Entry {public string path { get; set; } public string sha256 { get; set; }}
    public sealed class Manifest {
        public string product { get; set; }
        public string version { get; set; }
        public string platform { get; set; }
        public string entry { get; set; }
        public string[] compatible_sources { get; set; }
        public Entry[] files { get; set; }}
    const string Legacy="Lacuna-V1.1.55.exe", App="Lacuna.exe", Tool="升级一键成片.exe", Index="install-manifest.json";
    static string Hash(string file){using(var sha=SHA256.Create())using(var s=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","");}
    static bool Same(string a,string b){return String.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
    static string PathFor(string root,string relative)
    {
        if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":")||relative.Split('/','\\').Any(p=>p==""||p=="."||p==".."))
            throw new InvalidDataException("清单路径无效");
        string file=Path.GetFullPath(Path.Combine(root,relative));
        for(string p=file;p!=null;p=Path.GetDirectoryName(p))
        {
            try{if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("安装路径包含文件系统链接："+p);}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        return file;
    }
    static T Read<T>(string text){return new JavaScriptSerializer().Deserialize<T>(text);}
    static string Resource(string name){using(var r=new StreamReader(typeof(TeamInstaller).Assembly.GetManifestResourceStream(name)))return r.ReadToEnd();}
    static bool VersionIs(string file,string version)
    {
        if(!File.Exists(file))return false;
        var info=FileVersionInfo.GetVersionInfo(file);
        return info.ProductName=="Lacuna" && info.ProductVersion==version;
    }
    static Manifest Verify(string source)
    {
        var expected=Read<Manifest>(Resource("payload"));
        var m=Read<Manifest>(File.ReadAllText(PathFor(source,Index)));
        if(m.product!=expected.product || m.version!=expected.version || m.platform!=expected.platform || m.entry!=App ||
            m.compatible_sources==null || !m.compatible_sources.SequenceEqual(expected.compatible_sources) || m.files==null || m.files.Length!=expected.files.Length+1)
            throw new InvalidDataException("清单与本版升级程序不匹配");
        var entries=m.files.ToDictionary(e=>e.path,StringComparer.OrdinalIgnoreCase);
        foreach(var e in expected.files)if(!entries.ContainsKey(e.path)||!Same(e.sha256,entries[e.path].sha256))throw new InvalidDataException("清单内容不匹配："+e.path);
        if(!entries.ContainsKey(Tool)||!Same(entries[Tool].sha256,Hash(typeof(TeamInstaller).Assembly.Location)))throw new InvalidDataException("升级工具版本不匹配");
        foreach(var e in m.files)if(!Same(e.sha256,Hash(PathFor(source,e.path))))throw new InvalidDataException("文件校验失败，请重新完整解压："+e.path);
        if(!VersionIs(PathFor(source,App),m.version))throw new InvalidDataException("主程序版本不符");
        return m;
    }
    static void Links(string desktop,string app)
    {
        dynamic ws=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach(var name in new[]{"一键成片","一键成片（团队版）"})
        {
            string file=PathFor(desktop,name+".lnk");
            dynamic link=ws.CreateShortcut(file);link.TargetPath=app;link.Arguments="";link.WorkingDirectory=Path.GetDirectoryName(app);
            link.Description="Lacuna V1.2.0 团队版";link.IconLocation=app+",0";link.Save();
            if(!Same((string)ws.CreateShortcut(file).TargetPath,app))throw new IOException("请检查桌面快捷方式："+name);
        }
    }
    static bool Known(string dir)
    {
        return Directory.Exists(dir) && (VersionIs(PathFor(dir,Legacy),"V1.1.55") || VersionIs(PathFor(dir,App),"V1.1.6") || VersionIs(PathFor(dir,App),"V1.2.0"));
    }
    static string[] Find(string desktop,string source)
    {
        var dirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic ws=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        foreach(var name in new[]{"一键成片","一键成片（团队版）"})
        {
            string file=Path.Combine(desktop,name+".lnk");if(!File.Exists(file))continue;
            string target=ws.CreateShortcut(file).TargetPath;
            if(!String.IsNullOrWhiteSpace(target)){string dir=Path.GetDirectoryName(target);if(!Same(source,dir)&&Known(dir))dirs.Add(dir);}
        }
        return dirs.ToArray();
    }
    static void WriteVerified(string src,string dest,string hash)
    {
        if(File.Exists(dest) && Same(Hash(dest),hash))return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        string tmp=dest+"."+Guid.NewGuid().ToString("N")+".part";
        try
        {
            File.Copy(src,tmp);
            if(!Same(Hash(tmp),hash))throw new IOException("写入后校验失败："+dest);
            if(File.Exists(dest))File.Replace(tmp,dest,null);else File.Move(tmp,dest);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    internal static void Install(string source,string target,string desktop)
    {
        source=Path.GetFullPath(source).TrimEnd('\\');target=Path.GetFullPath(target).TrimEnd('\\');
        if(!Same(source,target) && (source.StartsWith(target+"\\",StringComparison.OrdinalIgnoreCase)||target.StartsWith(source+"\\",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("请把新版解压到与原安装位置分开的文件夹。");
        var m=Verify(source);
        if(!Known(target))throw new IOException("请选择 V1.1.55、V1.1.6 或 V1.2.0 团队版目录。首次使用可选完整解压目录。");
        var old = Read<Entry[]>(Resource("legacy1155")).Concat(Read<Entry[]>(Resource("legacy116")))
            .ToLookup(e => e.path, e => e.sha256, StringComparer.OrdinalIgnoreCase);
        string legacy = PathFor(target,Legacy), app = PathFor(target,App);
        var all=m.files.Concat(new[]{new Entry{path=Index,sha256=Hash(PathFor(source,Index))}}).OrderBy(e=>e.path==App?1:0).ToArray();
        var retained = new HashSet<string>(all.Select(e => e.path), StringComparer.OrdinalIgnoreCase);
        var obsolete = old.Where(group => !retained.Contains(group.Key)).ToList();
        foreach (var group in obsolete)
        {
            string path = PathFor(target,group.Key);
            if(File.Exists(path) && !group.Contains(Hash(path), StringComparer.OrdinalIgnoreCase))
                throw new IOException("旧文件与正式发行包不符，请检查：" + path);
        }
        foreach(var e in all)
        {
            string dest=PathFor(target,e.path);
            if(!File.Exists(dest))continue;
            string current=Hash(dest);if(Same(current,e.sha256))continue;
            if(!old[e.path].Contains(current,StringComparer.OrdinalIgnoreCase))throw new IOException("目标文件与原发行包不符，请检查："+dest);
        }
        foreach(string exe in new[]{app,legacy})if(File.Exists(exe))using(File.Open(exe,FileMode.Open,FileAccess.Read,FileShare.None)){}
        foreach(var e in all)WriteVerified(PathFor(source,e.path),PathFor(target,e.path),e.sha256);
        foreach(var e in all)if(!Same(Hash(PathFor(target,e.path)),e.sha256))throw new IOException("安装校验未通过："+e.path);
        Links(desktop,app);
        foreach (var group in obsolete)
        {
            string file = PathFor(target,group.Key);
            if(File.Exists(file) && group.Contains(Hash(file), StringComparer.OrdinalIgnoreCase)) File.Delete(file);
        }
    }
    [STAThread] static void Main()
    {
        Application.EnableVisualStyles();
        string source=Path.GetDirectoryName(typeof(TeamInstaller).Assembly.Location);
        string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        using(var gate=new Mutex(false,@"Local\LacunaTeamInstaller"))
        {
            bool entered=false;
            try
            {
                try{entered=gate.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
                if(!entered)throw new IOException("另一个更新窗口正在运行。");
                var dirs=Find(desktop,source);
                string target=null;
                if(dirs.Length==1 && MessageBox.Show("更新这个安装位置？\n"+dirs[0]+"\n\n选「否」可自行选择。","升级一键成片",MessageBoxButtons.YesNo)==DialogResult.Yes)target=dirs[0];
                if(target==null)using(var picker=new FolderBrowserDialog{Description="请选择原团队版程序所在文件夹。首次使用可选当前完整解压目录。",SelectedPath=source,ShowNewFolderButton=false})
                {if(picker.ShowDialog()!=DialogResult.OK)return;target=picker.SelectedPath;}
                if(MessageBox.Show("请保存制作进度并退出 Lacuna，再点确定。\n安装位置："+target,"升级一键成片",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
                Install(source,target,desktop);
                MessageBox.Show("已安装 V1.2.0，请通过桌面「一键成片」继续原项目。","升级完成");
            }
            catch(Exception ex){MessageBox.Show("未完成升级。请关闭运行中的程序、核对目录并重试。\n"+ex.GetBaseException().Message,"升级一键成片");}
            finally{if(entered)gate.ReleaseMutex();}
        }
    }
}

