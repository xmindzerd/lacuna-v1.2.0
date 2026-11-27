using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    public sealed class SkillReference
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Digest { get; set; }
        public List<SkillReference> Members { get; set; }
        public string Resolution { get; set; }
        public string RepairAgainst { get; set; }
    }

    internal sealed class LocalSkill
    {
        internal string Id, Name, Description, Location, Directory;
    }

    internal sealed class SelectedSkill
    {
        internal SkillReference Reference;
        internal string Body, Directory, Stamp;
        internal List<SelectedSkill> Parts;
        internal bool Checked;
        internal bool PreloadedReferences;
        internal string CheckedConnection;
    }

    internal static partial class LocalSkills
    {
        private const int MaxTextBytes = 128 * 1024;
        private static readonly string[] Stages = { "script", "assets", "storyboard", "segments", "video-prompt", "video", "delivery" };

        internal static string StageKey(int index) { return "stage." + Stages[index]; }

        internal static List<LocalSkill> List(string project)
        {
            var results = new List<LocalSkill>();
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!String.IsNullOrEmpty(project))
            {
                ScanRoot(project, @".agents\skills", "project", "当前项目", results, visited);
                ScanRoot(project, @".codex\skills", "project-codex", "当前项目 · Codex", results, visited);
            }
            ScanRoot(user, @".agents\skills", "agents", "本机用户", results, visited);
            string codex = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrWhiteSpace(codex))
                ScanRoot(codex, "skills", "codex-home", "本机 Codex", results, visited);
            ScanRoot(user, @".codex\skills", "codex", "本机 Codex", results, visited);
            return results.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ScanRoot(string parent, string relative, string scope, string location,
            List<LocalSkill> results, HashSet<string> visited)
        {
            if (String.IsNullOrWhiteSpace(parent)) return;
            bool allowLinks = scope != "project" && scope != "project-codex";
            string root;
            try
            {
                root = allowLinks ? Path.GetFullPath(Path.Combine(parent, relative)) : AgentData.Inside(parent, relative);
            }
            catch (InvalidOperationException) { return; }
            catch (ArgumentException) { return; }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (visited.Add(root))
                Scan(root, "", scope, location, 0, results, allowLinks);
        }

        private static void Scan(string directory, string relative, string scope, string location, int depth,
            List<LocalSkill> results, bool allowLinks)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                if (!allowLinks && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return;
                string file = Path.Combine(directory, "SKILL.md");
                if (File.Exists(file))
                {
                    if (!allowLinks && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                    string text = ReadText(file);
                    var metadata = Regex.Match(text, @"\A\s*---[ \t]*\r?\n([\s\S]*?)\r?\n---(?:\s|$)");
                    if (!metadata.Success) return;
                    string name = Property(metadata.Groups[1].Value, "name");
                    if (!String.IsNullOrWhiteSpace(name))
                        results.Add(new LocalSkill
                        {
                            Id = scope + "/" + relative.Replace('\\', '/'),
                            Name = name, Description = Property(metadata.Groups[1].Value, "description"),
                            Location = location, Directory = directory
                        });
                    return;
                }
                if (depth < 4)
                    foreach (string child in Directory.EnumerateDirectories(directory))
                        Scan(child, Path.Combine(relative, Path.GetFileName(child)), scope, location, depth + 1, results, allowLinks);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Property(string text, string key)
        {
            var value = Regex.Match(text, "(?m)^" + key + @":[ \t]*([^\r\n]*)(?:(?:\r?\n)[ \t]+([^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*))?");
            if (!value.Success) return "";
            string main = value.Groups[1].Value.Trim();
            string extra = Regex.Replace(value.Groups[2].Value.Trim(), @"\s+", " ");
            if (main.Length == 0 || main[0] == '>' || main[0] == '|') return extra;
            return main.Trim('"', '\'') + (extra.Length == 0 ? "" : " " + extra);
        }

        private static string Settings(string project)
        {
            return AgentData.Inside(AgentData.DirectoryFor(project), "Skill 选择.json");
        }

        private static Dictionary<string, SkillReference> Preferences(string project)
        {
            return AgentData.Read<Dictionary<string, SkillReference>>(Settings(project)) ?? new Dictionary<string, SkillReference>();
        }

        internal static SkillReference Choice(string project, string operation)
        {
            if (FlowSkills.Managed(project, operation)) return FlowSkills.Choice(project, operation);
            SkillReference value;
            return Preferences(project).TryGetValue(operation, out value) ? value : null;
        }

        internal static void SetChoice(string project, string operation, LocalSkill skill)
        {
            using (AnalysisData.Lock(project))
            {
                var preferences = Preferences(project);
                if (skill == null) preferences.Remove(operation);
                else preferences[operation] = new SkillReference { Id = skill.Id, Name = skill.Name };
                AgentData.Save(Settings(project), preferences);
            }
        }

        internal static SelectedSkill Resolve(string project, SkillReference reference)
        {
            if (reference == null) return null;
            if ((reference.Id ?? "").StartsWith("builtin/", StringComparison.Ordinal)) return BuiltinSkills.Resolve(reference.Id.Substring(8));
            if ((reference.Id ?? "").StartsWith("flow:", StringComparison.Ordinal)) return FlowSkills.Resolve(project, reference);
            if (reference.Members != null) return Combine(project, reference);
            var skill = List(project).FirstOrDefault(item => item.Id == reference.Id);
            if (skill == null)
                throw new InvalidOperationException("找不到已选择的 Skill「" + reference.Name + "」。请从本机列表重新选择或清除。");
            string body = ReadSkillFile(skill.Directory, "SKILL.md");
            string digest = AgentData.Hash(body);
            return new SelectedSkill
            {
                Body = body, Directory = skill.Directory,
                Reference = new SkillReference { Id = skill.Id, Name = skill.Name, Digest = digest }
            };
        }

        internal static string InsideSkill(string directory, string relative)
        {
            if (String.IsNullOrEmpty(directory)) throw new InvalidOperationException("本轮没有选定 Skill。");
            string folder = Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(Path.Combine(folder, relative ?? ""));
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                !file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || file == folder)
                throw new InvalidOperationException("文件引用超出当前 Skill 范围。");
            return file;
        }

        internal static string ReadSkillFile(string directory, string relative)
        {
            string file = InsideSkill(directory, relative);
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (!new[] { ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv" }.Contains(extension))
                throw new InvalidOperationException("只能读取此 Skill 目录中的文本参考文件。");
            return ReadText(file);
        }

        private static string ReadText(string file)
        {
            if (new FileInfo(file).Length > MaxTextBytes) throw new IOException("Skill 文本超过 128 KB。");
            return File.ReadAllText(file);
        }
    }
}
