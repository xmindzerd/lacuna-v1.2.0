using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneClickFilm
{
    public sealed class FlowSkillFile
    {
        public string Name, Digest;
    }

    public sealed class FlowSkillRoute
    {
        public string stage, guide;
        public List<int> members;
    }

    public sealed class FlowConflict
    {
        public string stage, point, reason;
        public int first, second, recommended;
    }

    public sealed class FlowDecision
    {
        public string Stage, Point, Reason;
        public int Selected, Excluded;
        public bool ByCodex;
    }

    public sealed class FlowSkillPlan
    {
        public string Key;
        public List<FlowSkillFile> Skills;
        public List<FlowSkillRoute> routes;
        public List<FlowConflict> conflicts;
        public List<FlowDecision> Decisions = new List<FlowDecision>();
    }

    internal sealed class FlowPackage
    {
        internal string Directory;
        internal FlowSkillFile Skill;
        internal List<string> Files;
    }

    internal static class FlowSkills
    {
        internal static readonly string[] Stages = { "script", "asset-prompt", "image", "storyboard", "segments", "final-prompt" };
        internal static readonly string[] Titles = { "剧本分析与资产清单", "资产提示词", "资产出图", "分镜表", "片段编排", "最终片段提示词" };
        private static readonly string[] Operations = { LocalSkills.StageKey(0), LocalSkills.StageKey(1), ImageOutputSettings.SkillOperation,
            LocalSkills.StageKey(2), LocalSkills.StageKey(3), LocalSkills.StageKey(4) };
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(
            new[] { ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv" }, StringComparer.OrdinalIgnoreCase);
        internal static string Title(string stage) { return Titles[Array.IndexOf(Stages, stage)]; }
        internal static string PlanPath(string root) { return AgentData.Inside(root, @"一键成片\Skill方案.json"); }
        private static string CachePath(string key) { return Path.Combine(AgentData.Local, "一键成片组合", key + ".json"); }

        internal static FlowPackage Inspect(string file)
        {
            file = Path.GetFullPath(file);
            if (!String.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择 Skill 文件夹中的 SKILL.md。");
            string folder = Path.GetDirectoryName(file);
            string body = LocalSkills.ReadSkillFile(folder, "SKILL.md");
            var match = Regex.Match(body, @"\A\s*---\s*\r?\n[\s\S]*?^name:\s*([^\r\n]+)", RegexOptions.Multiline);
            string name = match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : Path.GetFileName(folder);
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(body)) throw new InvalidDataException("Skill 缺少名称或正文。");
            var paths = new List<string>();
            var pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(folder, 0));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (string path in Directory.EnumerateFileSystemEntries(current.Key))
                {
                    var attrs = File.GetAttributes(path);
                    string part = Path.GetFileName(path);
                    if (part.StartsWith(".")) continue;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if (current.Value < 6 && part != "node_modules" && part != "__pycache__")
                            pending.Push(new KeyValuePair<string, int>(path, current.Value + 1));
                    }
                    else if (TextExtensions.Contains(Path.GetExtension(path)))
                    {
                        paths.Add(path.Substring(folder.Length + 1));
                        if (paths.Count > 400) throw new InvalidDataException("Skill 包含过多文本文件，请检查目录选择。");
                    }
                }
            }
            paths.Sort(StringComparer.Ordinal);
            var stamp = new StringBuilder();
            foreach (var path in paths)
                stamp.Append(path.Replace('\\','/')).Append('\t').Append(SystemData.Digest(LocalSkills.InsideSkill(folder, path))).Append('\n');
            return new FlowPackage { Directory = folder, Files = paths,
                Skill = new FlowSkillFile { Name = name, Digest = AgentData.Hash(stamp.ToString()) } };
        }

        internal static FlowSkillPlan CreateV2Plan(FlowDraft draft, out List<FlowPackage> packages)
        {
            draft.BuiltinPromptSkillId = BuiltinSkills.DefaultFinalPromptId;
            packages = BuiltinSkills.UpstreamPackages();
            bool custom = draft.FinalPromptSkillMode == "custom";
            if (custom && !File.Exists(draft.FinalPromptSkillFile))
                throw new FileNotFoundException("请选择自定义最终提示词 Skill 的 SKILL.md。");
            var final = custom ? Inspect(draft.FinalPromptSkillFile) :
                BuiltinSkills.Package(draft.BuiltinPromptSkillId);
            int finalIndex = packages.FindIndex(p => p.Skill.Digest == final.Skill.Digest);
            if (finalIndex < 0) { finalIndex = packages.Count; packages.Add(final); }
            var members = new[] { new[]{1,2}, new[]{3}, new int[0], new[]{4}, new int[0], new[]{finalIndex+1} };
            var guides = new[] {
                "1A 逐场导演剧本分析完成后，将原剧本和导演分析交给 1B Zenstory 拆解视觉设定与资产。",
                "按 2A 融合规范由 1B 直接整理横屏 4:3 人物五视图、场景空镜或影棚道具图，满幅无黑边灰边，按道具设定处理字迹。",
                "按共用身份或结构基础图 → 分项基准成图 → 同套服装或固定结构的状态变化派生；全局参考图仅控制画风。",
                "竖屏分镜承接原剧本、1A 导演要点与必需镜头、1B 视觉设定与连戏锁，以及 2B 已通过资产。",
                "软件增强分段器按 ≤30 秒与参考图上限编排，保护跨镜长台词与双语对白。",
                "按所选" + (custom ? "自定义" : "内置") + "最终提示词 Skill 的原格式保真生成。"
            };
            var plan = new FlowSkillPlan { Skills=packages.Select(p=>p.Skill).ToList(),
                conflicts=new List<FlowConflict>(), routes=Stages.Select((stage,i)=>new FlowSkillRoute {
                    stage=stage, members=members[i].ToList(), guide=guides[i] }).ToList() };
            plan.Key=AgentData.Hash("v2-builtin|" + String.Join("|", plan.Skills.Select(p=>p.Digest)) + "|" + finalIndex);
            Validate(plan);
            return plan;
        }

        internal static List<FlowPackage> Packages(IEnumerable<string> files)
        {
            return files.Select(Inspect).GroupBy(p => p.Skill.Digest).Select(g => g.First())
                .OrderBy(p => p.Skill.Digest, StringComparer.Ordinal).ToList();
        }

        internal static string Key(IEnumerable<FlowSkillFile> skills)
        {
            string key = AgentData.Hash("Lacuna.flow.1\n" + String.Join("\n", skills.Select(p => p.Digest)));
            return CodexAuthMode.IsApi ? AgentData.Hash(key + "\nlacuna_api\n" + CodexAuthMode.BaseUrl + "\n" +
                CodexAuthMode.EffectiveModel(AgentData.Options().Model)) : key;
        }

        internal static string PackageFolder(string root, FlowSkillFile skill)
        {
            if (!Regex.IsMatch(skill.Digest ?? "", "^[A-F0-9]{64}$")) throw new InvalidDataException("Skill 指纹无效。");
            return AgentData.Inside(root, Path.Combine("一键成片", "Skills", skill.Digest));
        }

        internal static void Snapshot(string root, IEnumerable<FlowPackage> packages)
        {
            foreach (var p in packages)
            {
                string folder = PackageFolder(root, p.Skill);
                foreach (var path in p.Files)
                {
                    string target = AgentData.Inside(folder, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(LocalSkills.InsideSkill(p.Directory, path), target, false);
                }
                if (Inspect(Path.Combine(folder, "SKILL.md")).Skill.Digest != p.Skill.Digest)
                    throw new IOException(p.Skill.Name + " 在创建期间发生变化，请重新检查组合。");
            }
        }

        private static SelectedSkill Bundle(IList<FlowPackage> packages)
        {
            var parts = packages.Select(p => new SelectedSkill { Directory = p.Directory, Body = LocalSkills.ReadSkillFile(p.Directory, "SKILL.md"),
                Reference = new SkillReference { Id = "snapshot/" + p.Skill.Digest, Name = p.Skill.Name, Digest = p.Skill.Digest } }).ToList();
            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];
            return new SelectedSkill { Parts = parts, Checked = true,
                Directory = "bundle:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(AgentData.Json().Serialize(parts.Select(p => p.Directory).ToArray()))),
                Body = String.Join("\n\n", parts.Select((p,i) => "【Skill " + (i+1) + " · " + p.Reference.Name + "】\n" + p.Body)),
                Reference = new SkillReference { Name = String.Join(" + ", parts.Select(p => p.Reference.Name)), Members = parts.Select(p => p.Reference).ToList() } };
        }

        internal static FlowSkillPlan Cached(List<FlowPackage> packages)
        {
            var key = Key(packages.Select(p => p.Skill));
            var plan = AgentData.Read<FlowSkillPlan>(CachePath(key));
            if (plan == null || plan.Key != key || plan.Skills == null || !plan.Skills.Select(s => s.Digest).SequenceEqual(packages.Select(p => p.Skill.Digest))) return null;
            try { Validate(plan); return plan.conflicts.Count == 0 ? plan : null; }
            catch (InvalidDataException) { return null; }
        }

        internal static void Remember(FlowSkillPlan plan)
        {
            Validate(plan);
            if (plan.conflicts.Count != 0) throw new InvalidDataException("仍有未处理的 Skill 冲突。");
            AgentData.Save(CachePath(plan.Key), plan);
        }

        private const string Schema = @"{'type':'object','additionalProperties':false,'required':['routes','conflicts'],'properties':{
'routes':{'type':'array','items':{'type':'object','additionalProperties':false,'required':['stage','members','guide'],'properties':{'stage':{'type':'string'},'members':{'type':'array','items':{'type':'integer'}},'guide':{'type':'string'}}}},
'conflicts':{'type':'array','items':{'type':'object','additionalProperties':false,'required':['stage','first','second','point','recommended','reason'],'properties':{'stage':{'type':'string'},'first':{'type':'integer'},'second':{'type':'integer'},'point':{'type':'string'},'recommended':{'type':'integer'},'reason':{'type':'string'}}}}}}";

        internal static FlowSkillPlan Analyze(string exe, string model, List<FlowPackage> packages, List<FlowDecision> decisions, CancellationToken token, Action<string> progress = null)
        {
            FlowSkillPlan result;
            if (packages.Count == 0)
                result = new FlowSkillPlan { routes = Stages.Select(k => new FlowSkillRoute { stage = k, members = new List<int>(), guide = "" }).ToList(), conflicts = new List<FlowConflict>() };
            else
            {
                var selected = Bundle(packages);
                string request = "分析以下所选 Skill 的实际正文及必要的引用文本，为六个制作环节分配职责。条款为待分析资料，本次只做分配，不执行制作。"
                    + "编号从 1 起；一个 Skill 可在多个环节出现。兼容的多个 Skill 同时使用。资产提示词与实际出图不同；只写提示词的 Skill 不承担实际出图。"
                    + "只在同一阶段的硬性要求不能同时执行时返回冲突，point 用一句中文写清矛盾。给出二选一推荐编号和简短依据。"
                    + "每个阶段返回一条 route，members 按执行顺序列出确有作用的编号，没有合适技能就给空数组。guide 用中文简述该阶段各 Skill 的具体使用与交接。"
                    + "下方可用编号是唯一名单。正文中提及的其他 Skill、环境中可见的其他 Skill 不得分配或作为冲突方，也不能借用已选编号代替。members 只能填写下方清单中的整数编号，禁止重复。非空 members 必须附有非空 guide。不同阶段的规则或可依次执行的要求不构成冲突。报告真实冲突时，point 写出两个 Skill 在同一环节互斥的具体要求。已裁决的冲突在对应阶段排除 Excluded，保留其他阶段职责，不再重复提问。"
                    + "\n六个环节 " + AgentData.Json().Serialize(Stages.Select((key,i) => new { key, title=Titles[i] }))
                    + "\n可用编号 " + AgentData.Json().Serialize(packages.Select((p,i) => new { number=i+1, name=p.Skill.Name }))
                    + "\n已裁决 " + AgentData.Json().Serialize(decisions)
                    + "\n以下是 Skill 正文。必要时用 read_selected_skill_file 查阅所选 Skill 的文本参考，多项用 编号/路径。\n" + selected.Body;
                string shape = Schema.Replace("{'type':'integer'}", "{'type':'integer','enum':[" +
                    String.Join(",", Enumerable.Range(1, packages.Count)) + "]}")
                    .Replace("'stage':{'type':'string'}", "'stage':{'type':'string','enum':" + AgentData.Json().Serialize(Stages) + "}");
                string correction = "";
                int corrections = 0;
                FlowSkillPlan pendingConflictCheck = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    result = FilmWriter.Ask<FlowSkillPlan>(exe, model, selected, shape, request + correction, token, null, true);
                    try
                    {
                        Complete(result, packages, decisions);
                        if (result.conflicts.Count == 0) return result;
                        if (pendingConflictCheck != null)
                        {
                            if (result.conflicts.All(c => pendingConflictCheck.conflicts.Any(p => p.stage == c.stage &&
                                Math.Min(p.first,p.second) == Math.Min(c.first,c.second) && Math.Max(p.first,p.second) == Math.Max(c.first,c.second))))
                                return result;
                            throw new InvalidDataException("冲突复核引入了上轮未报告的组合，需要重新核对依据。");
                        }
                        pendingConflictCheck = result;
                        if (progress != null) progress("Codex 正在对照所选 Skill 的原文复核冲突。");
                        correction = "\n上轮报告了以下潜在冲突。逐项独立复核，仅保留双方在同一环节确实无法同时执行的硬性要求，"
                            + "在 point 中分别列出两方的具体要求。有兼容执行方式时合并职责并移除该冲突。返回完整六阶段方案。\n"
                            + AgentData.Json().Serialize(new {result.routes, result.conflicts});
                    }
                    catch (InvalidDataException error)
                    {
                        string detail = error.Message;
                        if (corrections >= 2)
                            throw new InvalidDataException("Codex 分配及两次自动纠正后仍未通过。" + detail + " 创建草稿已保存。", error);
                        corrections++;
                        if (progress != null) progress(detail + " Codex 正在自动纠正（" + corrections + "/2）。");
                        correction = "\n上轮分配未通过校验：" + detail
                            + "\n请根据原文纠正问题，保留其他有效职责和已裁决决定，重新返回完整六阶段方案。上轮内容："
                            + AgentData.Json().Serialize(new {result.routes, result.conflicts});
                        if (pendingConflictCheck != null) correction += "\n仅复核以下候选冲突的原文依据：" + AgentData.Json().Serialize(pendingConflictCheck.conflicts);
                    }
                }
            }
            return Complete(result, packages, decisions);
        }

        private static FlowSkillPlan Complete(FlowSkillPlan result, List<FlowPackage> packages, List<FlowDecision> decisions)
        {
            result.Skills = packages.Select(p => p.Skill).ToList();
            result.Key = Key(result.Skills);
            result.Decisions = decisions.ToList();
            Validate(result);
            return result;
        }

        internal static void Validate(FlowSkillPlan plan)
        {
            if (plan.Skills == null || plan.Skills.Any(s => s == null || String.IsNullOrWhiteSpace(s.Name)) ||
                plan.Decisions == null || plan.routes == null || plan.conflicts == null || plan.routes.Count != Stages.Length ||
                plan.routes.Any(r => r == null) || Stages.Any(s => plan.routes.Count(r => r.stage == s) != 1))
                throw new InvalidDataException("Skill 方案没有覆盖六个制作环节。");
            foreach (var r in plan.routes)
            {
                string stage = Title(r.stage) + "：";
                if (r.members == null) throw new InvalidDataException(stage + "缺少成员列表 members。没有适用 Skill 时应返回空列表。");
                foreach (int id in r.members)
                    if (id < 1 || id > plan.Skills.Count)
                        throw new InvalidDataException(stage + "成员编号 " + id + " 超出所选 Skill 范围 1–" + plan.Skills.Count + "。");
                var repeated = r.members.GroupBy(id => id).FirstOrDefault(group => group.Count() > 1);
                if (repeated != null) throw new InvalidDataException(stage + "重复分配了 " + plan.Skills[repeated.Key-1].Name + "（编号 " + repeated.Key + "）。");
                if (r.members.Count > 0 && String.IsNullOrWhiteSpace(r.guide))
                    throw new InvalidDataException(stage + "已分配 " + String.Join("、", r.members.Select(id => plan.Skills[id-1].Name)) + "，缺少使用说明 guide。");
                foreach (var d in plan.Decisions.Where(d => d.Stage == r.stage && r.members.Contains(d.Excluded)))
                    throw new InvalidDataException(stage + "再次分配了此前已排除的 " + plan.Skills[d.Excluded-1].Name + "。");
            }
            foreach (var c in plan.conflicts)
                if (c == null || !Stages.Contains(c.stage) || c.first == c.second || c.first < 1 || c.second < 1 ||
                    c.first > plan.Skills.Count || c.second > plan.Skills.Count || (c.recommended != c.first && c.recommended != c.second)
                    || String.IsNullOrWhiteSpace(c.point) || String.IsNullOrWhiteSpace(c.reason) ||
                    plan.Decisions.Any(d => d.Stage == c.stage && (d.Excluded == c.first || d.Excluded == c.second)))
                    throw new InvalidDataException("Skill 冲突的依据、推荐或编号无效。");
        }

        internal static string StageForOperation(string operation) { return Stages[Array.IndexOf(Operations, operation)]; }

        internal static bool Managed(string root, string operation)
        {
            return root != null && Operations.Contains(operation) && File.Exists(PlanPath(root));
        }

        internal static SkillReference Choice(string root, string operation)
        {
            var plan = AgentData.Read<FlowSkillPlan>(PlanPath(root));
            if (plan == null || plan.routes == null || plan.Skills == null)
                throw new InvalidDataException("项目 Skill 方案缺失或已损坏。");
            string stage = Stages[Array.IndexOf(Operations, operation)];
            var route = plan.routes.FirstOrDefault(r => r != null && r.stage == stage);
            if (route == null || route.members == null || route.members.Count == 0) return null;
            var list = route.members.Where(i => i >= 1 && i <= plan.Skills.Count).Select(i => plan.Skills[i-1]).ToArray();
            if (list.Length == 0) return null;
            return new SkillReference { Id = "flow:" + stage, Name = String.Join(" + ", list.Select(s => s.Name)),
                Digest = AgentData.Hash(plan.Key + "\n" + String.Join("\n", list.Select(s => s.Digest)) + "\n" + route.guide), Resolution = route.guide };
        }

        internal static SelectedSkill Resolve(string root, SkillReference reference)
        {
            if (reference == null || String.IsNullOrEmpty(reference.Id) || !reference.Id.StartsWith("flow:", StringComparison.Ordinal))
                throw new InvalidDataException("未知的自动制作环节。");
            string stage = reference.Id.Substring(5);
            int index = Array.IndexOf(Stages, stage);
            if (index < 0) throw new InvalidDataException("未知的自动制作环节。");
            var expected = Choice(root, Operations[index]);
            if (expected == null || expected.Digest != reference.Digest) throw new InvalidDataException("项目 Skill 方案已更改，请重新打开制作步骤。");
            var plan = AgentData.Read<FlowSkillPlan>(PlanPath(root));
            if (plan == null || plan.routes == null || plan.Skills == null)
                throw new InvalidDataException("项目 Skill 方案缺失或已损坏。");
            var route = plan.routes.FirstOrDefault(r => r != null && r.stage == stage);
            if (route == null || route.members == null)
                throw new InvalidDataException("项目 Skill 方案缺少环节分配。");
            var packages = route.members.Select(i =>
            {
                var source = plan.Skills[i-1];
                string main = Path.Combine(PackageFolder(root, source), "SKILL.md");
                if (stage == "script" && !File.Exists(main) &&
                    (source.Name == BuiltinSkills.DirectorAnalysisId || source.Name == BuiltinSkills.VisualAssetsId))
                    return BuiltinSkills.Package(source.Name);
                var p = Inspect(main);
                if (p.Skill.Digest != source.Digest) throw new InvalidDataException(source.Name + " 的项目快照与创建时不同。");
                p.Skill.Name = source.Name;
                return p;
            }).ToList();
            var selected = Bundle(packages);
            expected.Digest = AgentData.Hash(plan.Key + "\n" + String.Join("\n", packages.Select(p => p.Skill.Digest)) + "\n" + route.guide);
            selected.Reference = expected;
            selected.Checked = true;
            selected.Body += "\n\n【当前环节 · " + Title(stage) + "】\n" + route.guide;
            return selected;
        }
    }
}
