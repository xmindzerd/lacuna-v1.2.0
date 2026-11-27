using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace OneClickFilm
{
    public sealed class BuiltinSkillInfo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Stage { get; set; }
        public string Digest { get; set; }
        public List<string> Files { get; set; }
    }

    internal static class BuiltinSkills
    {
        internal const string DirectorAnalysisId = "xiaoyu-director-script-analysis";
        internal const string VisualAssetsId = "short-drama-assets-zenstory";
        internal const string AssetImageRulesId = "lacuna-asset-image-rules";
        internal const string VerticalShotlistId = "xiaoyu-vertical-shotlist";
        internal const string DefaultFinalPromptId = "xiaoyu-seedance-one-click-prompts";

        private sealed class Manifest { public string version { get; set; } public List<Item> skills { get; set; } }
        private sealed class Item {
            public string id { get; set; }
            public string title { get; set; }
            public string stage { get; set; }
            public List<Member> files { get; set; }
        }
        private sealed class Member {
            public string path { get; set; }
            public long bytes { get; set; }
            public string sha256 { get; set; }
        }
        private static List<BuiltinSkillInfo> Verify(string root)
        {
            var manifest = AgentData.Read<Manifest>(Path.Combine(root, "manifest.json"));
            var ids = new[] {DirectorAnalysisId, VisualAssetsId, AssetImageRulesId, VerticalShotlistId, DefaultFinalPromptId};
            if (manifest == null || manifest.version != "V0.2.3" || manifest.skills == null || manifest.skills.Count != ids.Length ||
                manifest.skills.Any(s => s == null || s.files == null) || manifest.skills.Sum(s => s.files.Count) != 21 ||
                ids.Any(id => manifest.skills.Count(s => s != null && s.id == id) != 1))
                throw new InvalidDataException("内置 Skill 清单无效。");
            var result = new List<BuiltinSkillInfo>();
            foreach (var item in manifest.skills)
            {
                string directory = Path.Combine(root, item.id);
                if (item.files == null || !item.files.Any(m => m.path == "SKILL.md"))
                    throw new InvalidDataException("内置 Skill 缺少正文。");
                foreach (var member in item.files)
                    if (new FileInfo(LocalSkills.InsideSkill(directory, member.path)).Length != member.bytes ||
                        !String.Equals(SystemData.Digest(LocalSkills.InsideSkill(directory, member.path)), member.sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("内置 Skill 校验失败：" + member.path);
                var package = FlowSkills.Inspect(Path.Combine(directory, "SKILL.md"));
                if (package.Files.Count != item.files.Count || package.Files.Except(item.files.Select(m => m.path.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase).Any())
                    throw new InvalidDataException("内置 Skill 文件列表不一致。");
                result.Add(new BuiltinSkillInfo { Id=item.id, Title=item.title, Stage=item.stage, Digest=package.Skill.Digest,
                    Files=item.files.Select(m=>m.path).ToList() });
            }
            return result;
        }

        internal static string EnsureExtracted()
        {
            using (var stream = typeof(BuiltinSkills).Assembly.GetManifestResourceStream("BuiltinSkills.zip"))
            {
                if (stream == null)
                {
                    string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "builtin-skills");
                    Verify(source);
                    return source;
                }
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                stream.Position = 0;
                string root = Path.Combine(AgentData.Local, "内置Skill", hash.Substring(0,16));
                using (var mutex = new Mutex(false, "Local\\Lacuna.BuiltinSkills." + hash))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                    try
                    {
                        var manifest = zip.GetEntry("manifest.json");
                        if (manifest == null) throw new InvalidDataException("内置 Skill 缺少清单。");
                        string expected;
                        using (var input = manifest.Open())
                        using (var sha = SHA256.Create()) expected = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                        if (File.Exists(Path.Combine(root, "manifest.json")) &&
                            SystemData.Digest(Path.Combine(root, "manifest.json")) == expected)
                        {
                            try { Verify(root); return root; }
                            catch (InvalidDataException) { }
                            catch (IOException) { }
                        }
                        string staging = root + "." + Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(staging);
                        try
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (String.IsNullOrEmpty(entry.Name)) continue;
                                string file = LocalSkills.InsideSkill(staging, entry.FullName);
                                Directory.CreateDirectory(Path.GetDirectoryName(file));
                                using (var input = entry.Open())
                                using (var output = new FileStream(file, FileMode.CreateNew)) input.CopyTo(output);
                            }
                            Verify(staging);
                            string previous = staging + ".old";
                            if (Directory.Exists(root)) Directory.Move(root, previous);
                            try { Directory.Move(staging, root); }
                            catch { if (Directory.Exists(previous)) Directory.Move(previous, root); throw; }
                            if (Directory.Exists(previous)) Directory.Delete(previous, true);
                        }
                        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                        return root;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
        }

        internal static List<BuiltinSkillInfo> Catalog() { return Verify(EnsureExtracted()); }

        internal static FlowPackage Package(string id)
        {
            if (!new[]{DirectorAnalysisId,VisualAssetsId,AssetImageRulesId,VerticalShotlistId,DefaultFinalPromptId}.Contains(id))
                throw new InvalidDataException("未知内置 Skill。");
            return FlowSkills.Inspect(Path.Combine(EnsureExtracted(), id, "SKILL.md"));
        }

        internal static SelectedSkill Resolve(string id)
        {
            var p = Package(id);
            return new SelectedSkill { Reference=new SkillReference {Id="builtin/"+id,Name=p.Skill.Name,Digest=p.Skill.Digest},
                Checked=true, Directory=p.Directory, Body=LocalSkills.ReadSkillFile(p.Directory,"SKILL.md") };
        }

        // Project snapshots use their package digest as directory name. A custom skill can have the same title.
        private static readonly Lazy<Dictionary<string,string>> TextPackageDigests = new Lazy<Dictionary<string,string>>(
            () => Catalog().Where(p => p.Id != AssetImageRulesId).ToDictionary(p => p.Id,p => p.Digest));

        internal static SelectedSkill WithPreloadedReferences(SelectedSkill skill, params string[] relativePaths)
        {
            if (skill == null || String.IsNullOrEmpty(skill.Directory) || skill.Directory.StartsWith("bundle:", StringComparison.Ordinal))
                return skill;
            var reference = skill.Reference;
            string digest;
            if (reference == null || reference.Name == null || !TextPackageDigests.Value.TryGetValue(reference.Name,out digest)) return skill;
            if (reference.Id != "builtin/" + reference.Name && reference.Id != "snapshot/" + digest &&
                !(reference.Id != null && reference.Id.StartsWith("flow:",StringComparison.Ordinal) &&
                  Path.GetFileName(skill.Directory) == digest)) return skill;
            var sections = relativePaths.Where(path => File.Exists(LocalSkills.InsideSkill(skill.Directory, path)))
                .Select(path => "### " + path + "\n" + LocalSkills.ReadSkillFile(skill.Directory, path)).ToArray();
            return new SelectedSkill { Reference = reference, Directory = skill.Directory, Stamp = skill.Stamp,
                Parts = skill.Parts, Checked = skill.Checked, CheckedConnection = skill.CheckedConnection,
                Body = skill.Body + (sections.Length == 0 ? "" : "\n\n【已预载引用资料 · 无需再调用工具读取】\n" + String.Join("\n\n", sections)),
                PreloadedReferences = true };
        }

        internal static List<FlowPackage> UpstreamPackages()
        {
            return new[]{DirectorAnalysisId,VisualAssetsId,AssetImageRulesId,VerticalShotlistId}.Select(Package).ToList();
        }
    }
}
