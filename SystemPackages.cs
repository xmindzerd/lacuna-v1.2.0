using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    public sealed class HandoffItem
    {
        public string Name { get; set; }
        public string Sha256 { get; set; }
        public long Length { get; set; }
        public bool Script { get; set; }
    }

    public sealed class HandoffManifest
    {
        public string Format { get; set; }
        public string Id { get; set; }
        public string Project { get; set; }
        public string Brief { get; set; }
        public List<HandoffItem> Files { get; set; }
    }

    internal static class SystemPackages
    {
        private const long HandoffLimit = 8L * 1024 * 1024 * 1024;

        internal static void Export(string destination, string name, string brief, IList<string> sources, string script)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ||
                String.IsNullOrWhiteSpace(brief) || brief.Length > 8000 || sources.Count > 40)
                throw new InvalidOperationException("请输入名称和简报，简报最多 8000 字、附件最多 40 个。");
            var manifest = new HandoffManifest { Format = "oneclick-handoff-1", Id = AgentData.Id(),
                Project = name.Trim(), Brief = brief.Trim(), Files = new List<HandoffItem>() };
            string temp = destination + "." + AgentData.Id() + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    long total = 0;
                    for (int i = 0; i < sources.Count; i++)
                    {
                        string file = sources[i];
                        using (var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            total += from.Length;
                            if (total > HandoffLimit) throw new IOException("交接素材总大小超过 8 GB。");
                            string digest = SystemData.Digest(from);
                            from.Position = 0;
                            using (var target = zip.CreateEntry("data/" + i, CompressionLevel.Fastest).Open()) from.CopyTo(target);
                            manifest.Files.Add(new HandoffItem { Name = Path.GetFileName(file), Length = from.Length,
                                Sha256 = digest, Script = SystemData.Same(file, script) });
                        }
                    }
                    using (var json = new StreamWriter(zip.CreateEntry("handoff.json").Open(), new UTF8Encoding(false)))
                        json.Write(AgentData.Json().Serialize(manifest));
                }
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static string Import(string package, string root)
        {
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new InvalidOperationException("请先设置项目保存位置。");
            using (var stream = File.OpenRead(package))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var manifest = ReadJson<HandoffManifest>(zip.GetEntry("handoff.json"));
                if (manifest == null || manifest.Format != "oneclick-handoff-1" || !AgentData.ValidId(manifest.Id) ||
                    String.IsNullOrWhiteSpace(manifest.Project) || manifest.Project.Length > 100 ||
                    manifest.Brief == null || manifest.Brief.Length > 8000 || manifest.Files == null ||
                    manifest.Files.Count > 40 || zip.Entries.Count != manifest.Files.Count + 1 ||
                    manifest.Files.Any(f => f == null || f.Length < 0 || !ValidDigest(f.Sha256)) ||
                    manifest.Files.Count(x => x.Script) > 1)
                    throw new InvalidOperationException("交接清单格式不正确。");
                string fingerprint = AgentData.Hash(AgentData.Json().Serialize(manifest));
                var entries = new List<ZipArchiveEntry>();
                long total = 0;
                for (int i = 0; i < manifest.Files.Count; i++)
                {
                    var file = manifest.Files[i];
                    var entry = zip.GetEntry("data/" + i);
                    if (String.IsNullOrWhiteSpace(file.Name) || Path.GetFileName(file.Name) != file.Name ||
                        file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Name.Length > 180 ||
                        entry == null || entry.Length != file.Length || file.Length > HandoffLimit - total)
                        throw new IOException("交接包缺少有效附件：" + (file.Name ?? i.ToString()));
                    total += file.Length;
                    entries.Add(entry);
                }
                string folder = AgentData.Inside(root, "交接-" + manifest.Id.ToLowerInvariant());
                if (Directory.Exists(folder))
                {
                    var saved = AgentData.Read<Dictionary<string, string>>(AgentData.Inside(folder, "交接回执.json"));
                    string previous;
                    if (saved != null && saved.TryGetValue("Digest", out previous) && previous == fingerprint)
                    {
                        for (int i = 0; i < entries.Count; i++)
                            using (var attachment = entries[i].Open())
                                if (!String.Equals(SystemData.Digest(attachment), manifest.Files[i].Sha256, StringComparison.OrdinalIgnoreCase))
                                    throw new IOException("重复接收的附件内容发生变化：" + manifest.Files[i].Name);
                        return folder;
                    }
                    throw new InvalidOperationException("该交接编号已经存在，包内容与原回执不同。");
                }
                string stage = AgentData.Inside(root, ".交接导入-" + AgentData.Id());
                Directory.CreateDirectory(stage);
                try
                {
                    string importedScript = null;
                    for (int i = 0; i < manifest.Files.Count; i++)
                    {
                        var file = manifest.Files[i];
                        string relative = Path.Combine("交接素材", (i + 1) + "-" + file.Name);
                        string output = AgentData.Inside(stage, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        Extract(entries[i], output, file.Length, file.Sha256);
                        if (file.Script)
                        {
                            if (PdfScript.IsPdf(output)) PdfScript.Prepare(output, null, System.Threading.CancellationToken.None);
                            importedScript = relative;
                        }
                    }
                    AgentData.Save(Path.Combine(stage, "项目进度.json"), new ProjectProgress
                        { SchemaVersion = 1, Name = manifest.Project, Script = importedScript });
                    AgentData.Save(Path.Combine(stage, "交接简报.json"), manifest);
                    AgentData.Save(Path.Combine(stage, "交接回执.json"), new { Digest = fingerprint });
                    Directory.Move(stage, folder);
                    return folder;
                }
                finally { if (Directory.Exists(stage) && SystemData.Below(root, stage)) Directory.Delete(stage, true); }
            }
        }

        internal static bool ValidDigest(string hash)
        {
            return hash != null && Regex.IsMatch(hash, @"^[a-fA-F0-9]{64}$");
        }

        internal static T ReadJson<T>(ZipArchiveEntry entry) where T : class
        {
            if (entry == null || entry.Length > 256 * 1024)
                throw new InvalidOperationException("归档中缺少有效的 JSON 清单。");
            using (var content = entry.Open())
            using (var reader = new StreamReader(content, new UTF8Encoding(false, true)))
            {
                var buffer = new char[256 * 1024 + 1];
                int n = reader.ReadBlock(buffer, 0, buffer.Length);
                if (n == buffer.Length) throw new IOException("归档清单超过大小上限。");
                return AgentData.Json().Deserialize<T>(new string(buffer, 0, n));
            }
        }

        internal static void Extract(ZipArchiveEntry entry, string file, long length, string digest)
        {
            using (var source = entry.Open())
            using (var destination = new FileStream(file, FileMode.CreateNew))
                CopyBounded(source, destination, length);
            if (new FileInfo(file).Length != length ||
                !String.Equals(SystemData.Digest(file), digest, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件内容与清单校验值不一致：" + entry.Name);
        }

        internal static void CopyBounded(Stream source, Stream destination, long limit)
        {
            byte[] buffer = new byte[64 * 1024];
            int n;
            long total = 0;
            while ((n = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                total += n;
                if (total > limit) throw new IOException("文件超出清单大小或本版可接受的上限。");
                destination.Write(buffer, 0, n);
            }
        }
    }

    public sealed class UpdateManifest
    {
        public string Version { get; set; }
        public string File { get; set; }
        public string Url { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class StagedUpdate
    {
        internal UpdateManifest Manifest;
        internal string Folder, Executable;
    }

    internal static class SystemUpdate
    {
        private const int PackageLimit = 256 * 1024 * 1024;
        private static string ProgramFolder { get { return Path.GetDirectoryName(SystemData.Executable); } }
        private static string SelectionFile { get { return Path.Combine(ProgramFolder, "使用版本.json"); } }

        internal static void Select(StagedUpdate update)
        {
            AgentData.Save(SelectionFile, update.Manifest);
        }

        internal static bool OpenSelectedVersion()
        {
            try
            {
                var manifest = AgentData.Read<UpdateManifest>(SelectionFile);
                if (manifest == null || !SystemPackages.ValidDigest(manifest.Sha256)) return false;
                EnsureNewer(manifest.Version);
                if (manifest.File != "Lacuna-" + manifest.Version + ".exe") return false;
                string app = AgentData.Inside(ProgramFolder, manifest.File);
                if (!File.Exists(app) || !String.Equals(SystemData.Digest(app), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                var info = FileVersionInfo.GetVersionInfo(app);
                if (info.ProductName != "Lacuna" || info.ProductVersion != manifest.Version) return false;
                using (Process process = Process.Start(new ProcessStartInfo(app)
                    { UseShellExecute = true, WorkingDirectory = ProgramFolder }))
                    return process != null;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is InvalidOperationException || error is ArgumentException ||
                    error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception)) throw;
                return false;
            }
        }

        internal static StagedUpdate Fetch(string feedUrl)
        {
            UpdateManifest feed;
            var feedUri = Https(feedUrl);
            using (var body = new MemoryStream())
            {
                ReadUrl(feedUri, body, 65536);
                feed = AgentData.Json().Deserialize<UpdateManifest>(new UTF8Encoding(false, true).GetString(body.ToArray()));
            }
            if (feed == null || !SystemPackages.ValidDigest(feed.Sha256))
                throw new InvalidOperationException("更新源清单缺少完整的版本、地址或包校验值。");
            EnsureNewer(feed.Version);
            string download = AgentData.Inside(AgentData.Local, "更新下载-" + AgentData.Id() + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(download));
            try
            {
                using (var target = new FileStream(download, FileMode.CreateNew))
                    ReadUrl(Https(feed.Url), target, PackageLimit);
                if (!String.Equals(SystemData.Digest(download), feed.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("下载包与更新源的 SHA-256 不匹配。");
                return Inspect(download, feed.Version);
            }
            finally { if (File.Exists(download)) File.Delete(download); }
        }

        private static Uri Https(string value)
        {
            Uri url;
            if (!Uri.TryCreate(value, UriKind.Absolute, out url) || url.Scheme != "https" ||
                !String.IsNullOrEmpty(url.UserInfo) || !String.IsNullOrEmpty(url.Query) || !String.IsNullOrEmpty(url.Fragment))
                throw new InvalidOperationException("更新地址请填写公开的 HTTPS 文件链接。");
            return url;
        }

        private static void ReadUrl(Uri url, Stream target, long maximum)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 12000;
            request.ReadWriteTimeout = 20000;
            request.AllowAutoRedirect = false;
            using (var reply = (HttpWebResponse)request.GetResponse())
            {
                if (reply.StatusCode != HttpStatusCode.OK || reply.ContentLength > maximum)
                    throw new IOException("更新地址未交回可用的文件。");
                using (var source = reply.GetResponseStream()) SystemPackages.CopyBounded(source, target, maximum);
            }
        }

        internal static StagedUpdate Inspect(string zipFile, string expected = null)
        {
            string folder = AgentData.Inside(AgentData.Local, Path.Combine("更新暂存", AgentData.Id()));
            Directory.CreateDirectory(folder);
            try
            {
                using (var stream = File.OpenRead(zipFile))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var manifest = SystemPackages.ReadJson<UpdateManifest>(archive.GetEntry("update.json"));
                    if (manifest == null || !SystemPackages.ValidDigest(manifest.Sha256))
                        throw new InvalidOperationException("找不到有效的版本校验信息。");
                    EnsureNewer(manifest.Version);
                    if (expected != null && manifest.Version != expected)
                        throw new InvalidOperationException("包内版本与更新源清单不一致。");
                    string fileName = "Lacuna-" + manifest.Version + ".exe";
                    var package = archive.GetEntry(fileName);
                    if (manifest.File != fileName || package == null || archive.Entries.Count != 2 ||
                        package.Length <= 0 || package.Length > PackageLimit)
                        throw new InvalidOperationException("便携包里的程序名称或文件数无效。");
                    string file = AgentData.Inside(folder, fileName);
                    SystemPackages.Extract(package, file, package.Length, manifest.Sha256);
                    var info = FileVersionInfo.GetVersionInfo(file);
                    if (info.ProductVersion != manifest.Version || info.ProductName != "Lacuna")
                        throw new InvalidOperationException("程序的产品名或版本与清单不一致。");
                    return new StagedUpdate { Folder = folder, Executable = file, Manifest = manifest };
                }
            }
            catch { DeleteStage(folder); throw; }
        }

        internal static string Install(StagedUpdate update)
        {
            if (update == null) throw new InvalidOperationException("请先检查更新包。");
            EnsureNewer(update.Manifest.Version);
            if (!String.Equals(SystemData.Digest(update.Executable), update.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("已暂存的更新文件发生变化，请重新检查。");
            string destination = AgentData.Inside(ProgramFolder, update.Manifest.File);
            if (File.Exists(destination))
            {
                if (String.Equals(SystemData.Digest(destination), update.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    return destination;
                throw new IOException("已有同版本不同内容的文件，请从其他安装目录打开。");
            }
            string pending = destination + "." + AgentData.Id() + ".tmp";
            try
            {
                File.Copy(update.Executable, pending, false);
                if (!String.Equals(SystemData.Digest(pending), update.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("升级文件写入后的校验不一致。");
                File.Move(pending, destination);
                return destination;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }

        internal static void DeleteStage(string folder)
        {
            if (folder != null && SystemData.Below(Path.Combine(AgentData.Local, "更新暂存"), folder) &&
                Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(folder, true);
        }

        private static Version ReleaseOrder(Version version)
        {
            if (version.Major == 0 && version.Minor == 1)
            {
                if (version.Build == 11 || version.Build == 12) return new Version(0, 1, 1, version.Build - 10);
                if (version.Build >= 21 && version.Build <= 29) return new Version(0, 1, 2, version.Build - 20);
                if (version.Build >= 31 && version.Build <= 39) return new Version(0, 1, 3, (version.Build - 30) * 100 + Math.Max(0, version.Revision));
                if (version.Build >= 41 && version.Build <= 49) return new Version(0, 1, 4, (version.Build - 40) * 100 + Math.Max(0, version.Revision));
                if (version.Build >= 51 && version.Build <= 59) return new Version(0, 1, 5, (version.Build - 50) * 100 + Math.Max(0, version.Revision));
                if (version.Build >= 61 && version.Build <= 69) return new Version(0, 1, 6, (version.Build - 60) * 100 + Math.Max(0, version.Revision));
            }
            return version;
        }

        private static void EnsureNewer(string value)
        {
            var match = Regex.Match(value ?? "", @"^V(\d{1,4}\.\d{1,4}\.\d{1,4})(?:-patch([1-9]\d{0,3}))?$");
            Version offered;
            string number = match.Groups[1].Value + "." + (match.Groups[2].Success ? match.Groups[2].Value : "0");
            if (!match.Success || !Version.TryParse(number, out offered) || offered.Major != 1 ||
                ReleaseOrder(offered) <= ReleaseOrder(typeof(Program).Assembly.GetName().Version))
                throw new InvalidOperationException("更新包必须比当前 " + Program.Version + " 更新。");
        }
    }
}

