using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace OneClickFilm
{
    public sealed class ProjectProgress
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; }
        public string Script { get; set; }
        public string DialogueLanguage { get; set; }
        public string Category { get; set; }
        internal bool Automatic { get { return Category == "一键成片项目"; } }
        internal string CategoryLabel { get { return Automatic ? Category : "常规项目"; } }
    }

    public sealed class LocalPreferences
    {
        public string Root { get; set; }
        public string LastProject { get; set; }
        public List<RecentProject> RecentProjects { get; set; }
        public string CodexPath { get; set; }
        public SystemOptions System { get; set; }

        internal LocalPreferences Copy()
        {
            var clone = (LocalPreferences)MemberwiseClone();
            clone.RecentProjects = RecentProjects == null ? new List<RecentProject>() :
                RecentProjects.ConvertAll(r => r == null ? null : new RecentProject { Folder = r.Folder, Name = r.Name });
            clone.System = System == null ? new SystemOptions() : System.Copy();
            return clone;
        }
    }

    public sealed class RecentProject
    {
        public string Folder { get; set; }
        public string Name { get; set; }
    }

    internal sealed class ProjectStore
    {
        internal const string RootName = "一键成片";
        private const string ProgressFile = "项目进度.json";
        private readonly string preferencesPath;
        private readonly string previousPreferencesPath;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        internal ProjectStore(string preferencesPath, string previousPreferencesPath = null)
        {
            this.preferencesPath = preferencesPath;
            this.previousPreferencesPath = previousPreferencesPath;
        }

        internal LocalPreferences LoadPreferences()
        {
            string existing = File.Exists(preferencesPath) ? preferencesPath : previousPreferencesPath;
            if (String.IsNullOrEmpty(existing) || !File.Exists(existing))
                return new LocalPreferences();
            try
            {
                return AgentData.Read<LocalPreferences>(existing) ?? new LocalPreferences();
            }
            catch (ArgumentException) { return new LocalPreferences(); }
            catch (InvalidOperationException) { return new LocalPreferences(); }
        }

        internal void SavePreferences(LocalPreferences preferences)
        {
            AgentData.Save(preferencesPath, preferences);
        }

        internal string SelectRoot(string parent)
        {
            string root = Path.GetFullPath(Path.Combine(parent, RootName));
            Directory.CreateDirectory(root);
            return root;
        }

        internal string CreateProject(string root, string name, string language = null)
        {
            return Create(root, name, "常规项目", null, language);
        }

        internal string Create(string root, string name, string category, Action<string> initialize, string language = null)
        {
            if (category != "一键成片项目") throw new InvalidOperationException("团队版只支持创建一键成片项目。");
            name = (name ?? "").Trim();
            ValidateName(name);
            if (language != null && language != "英文" && language != "中文") throw new InvalidDataException("请选择短剧的对白语言。");

            string destination = Path.Combine(root, name);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("这个位置已有同名项目或文件。请改用其他名称。");

            string staging = Path.Combine(root, ".new-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                SaveJson(Path.Combine(staging, ProgressFile), new ProjectProgress
                {
                    SchemaVersion = 1,
                    Name = name, Category = category, DialogueLanguage = language
                });
                if (initialize != null) initialize(staging);
                Directory.Move(staging, destination);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
            return destination;
        }

        internal string TrashProject(LocalPreferences settings, string folder)
        {
            string library = Path.GetFullPath(settings.Root).TrimEnd('\\', '/');
            string target = Path.GetFullPath(folder).TrimEnd('\\', '/');
            string leaf = Path.GetFileName(target);
            if (!String.Equals(Path.GetDirectoryName(target), library, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(leaf, ".lacuna-trash", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只能删除当前项目库内的项目。");
            AgentData.Inside(library, leaf);
            OpenProject(target);
            string trash = AgentData.Inside(library, ".lacuna-trash");
            if (HasProgress(trash)) throw new InvalidOperationException("回收目录与已有项目重名。");
            string bin = AgentData.Inside(library, Path.Combine(".lacuna-trash", Guid.NewGuid().ToString("N")));
            string destination = Path.Combine(bin, leaf);
            var updated = settings.Copy();
            updated.RecentProjects.RemoveAll(p => p != null && SamePath(p.Folder, target));
            if (SamePath(updated.LastProject, target)) updated.LastProject = null;
            // Windows refuses to rename a directory while files below it remain open.
            // Probe existing production locks, then let the atomic move arbitrate new opens.
            foreach (string relative in new[] {@"Codex助手\运行.lock", @"一键成片\自动制作.lock", @"影片制作\视频任务.lock"})
            {
                string path = AgentData.Inside(target, relative);
                if (!File.Exists(path)) continue;
                try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                catch (FileNotFoundException) { }
                catch (IOException ex) { throw new IOException("项目文件正被占用，请等待制作任务结束后再删除。", ex); }
            }
            try
            {
                Directory.CreateDirectory(bin);
                Directory.Move(target, destination);
                try { SavePreferences(updated); }
                catch { Directory.Move(destination, target); throw; }
                settings.RecentProjects = updated.RecentProjects;
                settings.LastProject = updated.LastProject;
                return destination;
            }
            finally
            {
                if (Directory.Exists(bin) && !Directory.EnumerateFileSystemEntries(bin).Any())
                    Directory.Delete(bin);
            }
        }

        private static bool SamePath(string first, string fullPath)
        {
            return !String.IsNullOrWhiteSpace(first) && String.Equals(first.Replace('/', '\\').TrimEnd('\\'),
                fullPath, StringComparison.OrdinalIgnoreCase);
        }

        internal ProjectProgress OpenProject(string folder)
        {
            string path = Path.Combine(folder, ProgressFile);
            if (!File.Exists(path))
                throw new InvalidOperationException("选中的文件夹缺少项目进度文件。请选择一个 Lacuna 项目文件夹。");

            ProjectProgress progress;
            try
            {
                progress = AgentData.Read<ProjectProgress>(path);
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException("项目进度文件内容无效，无法打开该项目。", error);
            }
            catch (InvalidOperationException error)
            {
                throw new InvalidOperationException("项目进度文件内容无效，无法打开该项目。", error);
            }
            if (progress == null || progress.SchemaVersion != 1 || String.IsNullOrWhiteSpace(progress.Name))
                throw new InvalidOperationException("无法读取这个项目的进度格式。请选择 V0.1.1 创建的项目。");
            if (!progress.Automatic) throw new InvalidOperationException("团队版只支持打开一键成片项目。");
            if (!String.IsNullOrEmpty(progress.Script))
                ProjectFile(folder, progress.Script);
            return progress;
        }

        internal bool HasProgress(string folder)
        {
            if (!File.Exists(Path.Combine(folder, ProgressFile))) return false;
            try { return OpenProject(folder).Automatic; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal DateTime UpdatedAt(string folder)
        {
            return File.GetLastWriteTime(Path.Combine(folder, ProgressFile));
        }

        internal ProjectProgress ImportScript(string folder, string original, Action<string> prepare = null)
        {
            ProjectProgress progress = OpenProject(folder);
            string importFolder = AgentData.Inside(folder, Path.Combine("剧本", Guid.NewGuid().ToString("N")));
            string name = Path.GetFileName(original);
            string target = Path.Combine(importFolder, name);
            Directory.CreateDirectory(importFolder);
            try
            {
                File.Copy(original, target);
                if (prepare != null) prepare(target);
                progress.Script = Path.Combine("剧本", Path.GetFileName(importFolder), name);
                SaveJson(Path.Combine(folder, ProgressFile), progress);
            }
            catch
            {
                if (Directory.Exists(importFolder)) Directory.Delete(importFolder, true);
                throw;
            }
            return progress;
        }

        internal string ProjectFile(string folder, string relativePath)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (Path.IsPathRooted(relativePath) || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("项目文件引用超出了项目文件夹。");
            return full;
        }

        private void SaveJson(string path, object value)
        {
            AgentData.Save(path, value);
        }

        internal static void ValidateName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.EndsWith(".")
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("请输入可用的 Windows 文件夹名称。");
            if (name.Length > 80)
                throw new InvalidOperationException("项目名称请控制在 80 个字符以内，以免文件路径过长。");

            string firstPart = name.Split('.')[0].TrimEnd().ToUpperInvariant();
            const string reserved = "|CON|PRN|AUX|NUL|COM1|COM2|COM3|COM4|COM5|COM6|COM7|COM8|COM9|LPT1|LPT2|LPT3|LPT4|LPT5|LPT6|LPT7|LPT8|LPT9|";
            if (reserved.Contains("|" + firstPart + "|"))
                throw new InvalidOperationException("该名称由 Windows 保留。请改用其他名称。");
        }
    }
}
