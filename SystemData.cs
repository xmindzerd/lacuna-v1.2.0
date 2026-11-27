using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace OneClickFilm
{
    public sealed class SystemOptions
    {
        public bool AllowBuiltinTimeoutRetry { get; set; }
        public int ImageTimeoutMinutes { get; set; }
        public bool PlainWords { get; set; }
        public string Accent { get; set; }
        public int Zoom { get; set; }
        public bool HighContrast { get; set; }
        public bool StrongFocus { get; set; }
        public bool ReduceMotion { get; set; }
        public string ExportFolder { get; set; }
        public string UpdateFeed { get; set; }
        public string BoundFolder { get; set; }
        internal SystemOptions Copy() { return (SystemOptions)MemberwiseClone(); }
    }

    public sealed class SystemPointer
    {
        public string Folder { get; set; }
    }

    internal sealed class DataLocation
    {
        internal string Folder, Mode;
        internal bool Missing;
    }

    public sealed class LibraryFile
    {
        public string Path { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class LibraryReceipt
    {
        public string Source { get; set; }
        public string Destination { get; set; }
        public List<LibraryFile> Files { get; set; }
        public List<string> Folders { get; set; }
        public bool Recycled { get; set; }
    }

    internal static class SystemData
    {
        private static DataLocation active;
        internal static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
        internal static string PortableFolder { get { return Path.Combine(Path.GetDirectoryName(Executable), "便携资料"); } }
        private static string PortablePause { get { return Path.Combine(PortableFolder, ".停用便携"); } }
        private static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "一键成片"); }
        }
        private static string PointerFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "一键成片-入口", "资料位置.json"); }
        }

        internal static DataLocation Active { get { return active ?? (active = Resolve()); } }

        private static DataLocation Resolve()
        {
            string custom = Environment.GetEnvironmentVariable("ONE_CLICK_FILM_SETTINGS");
            if (!String.IsNullOrWhiteSpace(custom)) return From(custom, "环境变量");
            if (Directory.Exists(PortableFolder) && !File.Exists(PortablePause))
                return From(PortableFolder, "便携资料");
            try
            {
                var pointer = AgentData.Read<SystemPointer>(PointerFile);
                if (pointer != null && !String.IsNullOrWhiteSpace(pointer.Folder)) return From(pointer.Folder, "迁移位置");
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                    error is InvalidOperationException)) throw;
                return new DataLocation { Folder = PointerFile, Mode = "迁移位置", Missing = true };
            }
            return new DataLocation { Folder = DefaultFolder, Mode = "Windows 默认资料" };
        }

        private static DataLocation From(string path, string mode)
        {
            string full = Path.GetFullPath(path);
            return new DataLocation { Folder = full, Mode = mode, Missing = !Directory.Exists(full) };
        }

        internal static bool FindMissingLocation(DataLocation location)
        {
            if (location.Mode == "环境变量")
            {
                MessageBox.Show("环境变量指定的资料位置不可用。\n" + location.Folder, "Lacuna");
                return false;
            }
            if (MessageBox.Show("上次的应用资料位置不可用。请接回原盘或选择已移动的资料目录。\n" + location.Folder,
                "找回 Lacuna 资料", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return false;
            using (var dialog = new Forms.FolderBrowserDialog { Description = "选择包含 Lacuna 窗口设置的现有应用资料目录", ShowNewFolderButton = false })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return false;
                try
                {
                    string folder = Path.GetFullPath(dialog.SelectedPath);
                    if (!Directory.EnumerateFiles(folder, "窗口设置*.json", SearchOption.TopDirectoryOnly).Any())
                        throw new InvalidOperationException("所选文件夹没有 Lacuna 窗口设置。");
                    AgentData.Save(PointerFile, new SystemPointer { Folder = folder });
                    active = From(folder, "迁移位置");
                    return true;
                }
                catch (Exception e)
                {
                    MessageBox.Show(e.Message, "无法找回资料");
                    return false;
                }
            }
        }

        internal static void ActivateCopy(string destination, bool portable)
        {
            if (Active.Mode == "环境变量")
                throw new InvalidOperationException("当前资料位置由环境变量指定，请调整该变量后再切换。");
            if (portable)
            {
                if (!Same(PortableFolder, destination) || !Directory.Exists(PortableFolder))
                    throw new InvalidOperationException("便携资料目录尚未建立。");
            }
            else
            {
                AgentData.Save(PointerFile, new SystemPointer { Folder = destination });
                if (Active.Mode == "便携资料") File.WriteAllText(PortablePause, "历史便携资料", Encoding.UTF8);
            }
        }

        internal static bool Same(string a, string b)
        {
            return !String.IsNullOrWhiteSpace(a) && !String.IsNullOrWhiteSpace(b) &&
                String.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool Below(string root, string path)
        {
            return !String.IsNullOrWhiteSpace(root) && !String.IsNullOrWhiteSpace(path) &&
                Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static string Remap(string path, string source, string destination)
        {
            if (Same(source, path)) return destination;
            if (!Below(source, path)) return path;
            return Path.Combine(destination, Path.GetFullPath(path).Substring(Path.GetFullPath(source).TrimEnd('\\', '/').Length + 1));
        }

        internal static void RemapProjects(LocalPreferences prefs, string source, string destination)
        {
            prefs.Root = Remap(prefs.Root, source, destination);
            prefs.LastProject = Remap(prefs.LastProject, source, destination);
            prefs.RecentProjects = (prefs.RecentProjects ?? new List<RecentProject>()).Where(e => e != null)
                .Select(e => new RecentProject { Name = e.Name, Folder = Remap(e.Folder, source, destination) }).ToList();
            prefs.System = (prefs.System ?? new SystemOptions()).Copy();
            prefs.System.ExportFolder = Remap(prefs.System.ExportFolder, source, destination);
        }

        internal static string Digest(Stream input)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        internal static string Digest(string file)
        {
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Digest(input);
        }

        private static LibraryReceipt Inventory(string source, string destination)
        {
            var receipt = new LibraryReceipt { Source = source, Destination = destination,
                Files = new List<LibraryFile>(), Folders = new List<string>() };
            var pending = new Stack<string>();
            pending.Push(source);
            while (pending.Count > 0)
            {
                string item = pending.Pop();
                RejectLink(item);
                foreach (string file in Directory.EnumerateFiles(item))
                {
                    string leaf = Path.GetFileName(file);
                    if (Same(item, source) && (leaf == ".停用便携" || leaf == "系统迁移回执.json")) continue;
                    RejectLink(file);
                    receipt.Files.Add(new LibraryFile { Path = file.Substring(source.Length + 1),
                        Length = new FileInfo(file).Length, Sha256 = Digest(file) });
                }
                foreach (string child in Directory.EnumerateDirectories(item))
                {
                    receipt.Folders.Add(child.Substring(source.Length + 1));
                    pending.Push(child);
                }
            }
            return receipt;
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("资料包含链接文件或目录，请先核对：" + path);
        }

        internal static LibraryReceipt CopyLibrary(string source, string destination)
        {
            source = Path.GetFullPath(source).TrimEnd('\\', '/');
            destination = Path.GetFullPath(destination).TrimEnd('\\', '/');
            if (source.Length <= 3 || destination.Length <= 3 || Same(source, destination) ||
                Below(source, destination) || Below(destination, source) || !Directory.Exists(source) ||
                Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("请选择一个尚不存在的新目录，且它不能位于原库内部。");
            string parent = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(parent);
            var receipt = Inventory(source, destination);
            string volume = Path.GetPathRoot(destination);
            if (volume.Length == 3)
            {
                var disk = new DriveInfo(volume);
                if (disk.IsReady && disk.AvailableFreeSpace < receipt.Files.Sum(x => x.Length))
                    throw new IOException("目标盘可用空间不足。");
            }
            string temporary = Path.Combine(parent, ".一键成片迁移-" + AgentData.Id());
            Directory.CreateDirectory(temporary);
            try
            {
                foreach (string folder in receipt.Folders) Directory.CreateDirectory(AgentData.Inside(temporary, folder));
                foreach (var file in receipt.Files)
                {
                    string copied = AgentData.Inside(temporary, file.Path);
                    File.Copy(AgentData.Inside(source, file.Path), copied, false);
                    VerifyFile(temporary, file);
                }
                VerifyLibrary(source, receipt);
                Directory.Move(temporary, destination);
                return receipt;
            }
            finally
            {
                // Only the staging directory created for this operation is eligible for removal.
                if (Directory.Exists(temporary) && Below(parent, temporary))
                    Directory.Delete(temporary, true);
            }
        }

        private static void VerifyFile(string folder, LibraryFile file)
        {
            var path = AgentData.Inside(folder, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length || Digest(path) != file.Sha256)
                throw new IOException("文件内容校验失败：" + file.Path);
        }

        private static void VerifyLibrary(string folder, LibraryReceipt receipt)
        {
            var actual = Inventory(folder, null);
            var files = actual.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            if (files.Count != receipt.Files.Count ||
                !new HashSet<string>(actual.Folders, StringComparer.OrdinalIgnoreCase).SetEquals(receipt.Folders))
                throw new IOException("资料目录内容在校验期间变动。");
            foreach (var expected in receipt.Files)
            {
                LibraryFile item;
                if (!files.TryGetValue(expected.Path, out item) || item.Length != expected.Length || item.Sha256 != expected.Sha256)
                    throw new IOException("资料内容发生变化：" + expected.Path);
            }
        }

        internal static void RecycleOld(LibraryReceipt receipt, IEnumerable<string> liveLocations)
        {
            if (receipt == null || receipt.Recycled || receipt.Files == null || receipt.Folders == null ||
                Same(receipt.Source, receipt.Destination) || Below(receipt.Source, receipt.Destination) ||
                Below(receipt.Destination, receipt.Source) ||
                liveLocations.Any(p => Same(receipt.Source, p) || Below(receipt.Source, p)))
                throw new InvalidOperationException("这份旧库仍与在用位置有关，暂不可从这里清理。");
            string old = Path.GetFullPath(receipt.Source).TrimEnd('\\', '/');
            if (old.Length <= 3 || !Path.IsPathRooted(old) ||
                new DriveInfo(Path.GetPathRoot(old)).DriveType != DriveType.Fixed)
                throw new InvalidOperationException("旧库清理支持本机固定盘的可回收目录。");
            VerifyLibrary(old, receipt);
            foreach (var file in receipt.Files) VerifyFile(receipt.Destination, file);
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(old,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            receipt.Recycled = true;
        }

        internal static string ExportMedia(string source, string folder, string title)
        {
            if (!Directory.Exists(folder)) throw new IOException("导出目录已不可用。请重新选择。");
            if (!File.Exists(source)) throw new IOException("找不到这项制作产物。");
            string prefix = String.Concat((title ?? "制作产物").Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
            string baseName = AgentData.Short(prefix, 40) + "-" + Path.GetFileNameWithoutExtension(source);
            string ext = Path.GetExtension(source);
            var path = AgentData.Inside(folder, baseName + ext);
            for (int n = 2; File.Exists(path) && n < 1000; n++)
                path = AgentData.Inside(folder, baseName + " (" + n + ")" + ext);
            string staging = AgentData.Inside(folder, ".导出-" + AgentData.Id() + ".tmp");
            try
            {
                File.Copy(source, staging, false);
                File.Move(staging, path);
                return path;
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public sealed class SharedProjectNote
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    public sealed class ProjectNoteLink
    {
        public bool Include { get; set; }
        public SharedProjectNote Snapshot { get; set; }
    }

    internal static class SystemMemory
    {
        private static string Notes { get { return Path.Combine(AgentData.Local, "共享项目记忆"); } }

        internal static List<SharedProjectNote> List()
        {
            if (!Directory.Exists(Notes)) return new List<SharedProjectNote>();
            return Directory.EnumerateFiles(Notes, "*.json").Select(AgentData.Read<SharedProjectNote>)
                .Where(n => n != null && AgentData.ValidId(n.Id)).OrderBy(n => n.Title).ToList();
        }

        private static string LinkFile(string project)
        {
            return AgentData.Inside(project, "共享记忆.json");
        }

        internal static ProjectNoteLink Link(string project)
        {
            return project == null ? null : AgentData.Read<ProjectNoteLink>(LinkFile(project));
        }

        internal static void Save(string project, string id, string title, string text, bool include)
        {
            title = (title ?? "").Trim();
            text = (text ?? "").Trim();
            if (title.Length == 0 || title.Length > 100 || text.Length == 0 || text.Length > 4000)
                throw new InvalidOperationException("请输入笔记标题和最多 4000 字的内容。");
            var note = new SharedProjectNote { Id = AgentData.ValidId(id) ? id : AgentData.Id(), Title = title, Text = text };
            AgentData.Save(AgentData.Inside(Notes, note.Id + ".json"), note);
            SetLink(project, note, include);
        }

        internal static void SetLink(string project, SharedProjectNote note, bool include)
        {
            new ProjectStore(null).OpenProject(project);
            AgentData.Save(LinkFile(project), new ProjectNoteLink { Include = include, Snapshot = note });
        }

        internal static SharedProjectNote ForProject(string project)
        {
            var link = Link(project);
            if (link == null || !link.Include || link.Snapshot == null || !AgentData.ValidId(link.Snapshot.Id)) return null;
            string path = AgentData.Inside(Notes, link.Snapshot.Id + ".json");
            var current = AgentData.Read<SharedProjectNote>(path);
            var note = current != null && current.Id == link.Snapshot.Id ? current : link.Snapshot;
            return new SharedProjectNote { Id = note.Id, Title = AgentData.Short(note.Title, 100), Text = AgentData.Short(note.Text, 4000) };
        }

        internal static string PromptContext(string project)
        {
            var note = ForProject(project);
            return note == null ? "" : "\n\n当前项目已授权引用的共享记忆，仅供制作参考：\n" + note.Title + "\n" + note.Text;
        }
    }
}
