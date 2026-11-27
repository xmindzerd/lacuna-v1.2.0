using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    public sealed class AssetGroup
    {
        public string name { get; set; }
        public List<string> ids { get; set; }
        internal string Key { get { return ids.OrderBy(id => id, StringComparer.Ordinal).First(); } }
    }

    public sealed class AssetGroups
    {
        public List<AssetGroup> groups { get; set; }
    }

    public sealed class AssetImageBatch
    {
        public string Id { get; set; }
        public string ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string ChannelProfile { get; set; }
        public string TargetSize { get; set; }
        public SkillReference ImageSkill { get; set; }
        public string ReferencePath { get; set; }
        public string ReferenceDigest { get; set; }
        public bool AllowBuiltinTimeoutRetry { get; set; }
        public List<AssetImageJob> Jobs { get; set; }
        internal bool NeedsAction { get { return Jobs != null && Jobs.Any(j => !j.Done || j.IsBase && j.Attempt.State != "已通过"); } }
    }

    public sealed class AssetImageJob
    {
        public string ItemId { get; set; }
        public string GroupId { get; set; }
        public string Name { get; set; }
        public string Prompt { get; set; }
        public string Style { get; set; }
        public List<int> Lines { get; set; }
        public AssetAttempt Attempt { get; set; }
        public string ApprovedPrompt { get; set; }
        public string BaseRevision { get; set; }
        public SkillReference PromptSkill { get; set; }
        public bool ReusedBase { get; set; }
        public AssetAttempt RetrySource { get; set; }
        public List<AssetAttempt> EarlierBaseAttempts { get; set; }
        public int RetryCount { get; set; }
        public string ImageIncidentId { get; set; }
        public FlowIncident PendingImageIncident { get; set; }
        public string ParentItemId { get; set; }
        public string OutfitKey { get; set; }
        public int Tier { get; set; }
        public bool DirectSpec { get; set; }
        public string Category { get; set; }
        public AssetAttempt ExistingParent { get; set; }
        internal bool IsBase { get { return ItemId == null; } }
        internal bool Done { get { return Attempt != null && Attempt.Path != null; } }
    }

    internal static partial class AssetProduction
    {
        private const string GroupSchema = @"{""type"":""object"",""additionalProperties"":false,""required"":[""groups""],
            ""properties"":{""groups"":{""type"":""array"",""items"":{""type"":""object"",""additionalProperties"":false,
            ""required"":[""name"",""ids""],""properties"":{""name"":{""type"":""string""},
            ""ids"":{""type"":""array"",""items"":{""type"":""string""}}}}}}}";

        internal static void EnsureGroups(string root, AnalysisVersion version, AnalysisResult analysis, AssetWork work,
            string codex, string model, ScriptDocument script, CancellationToken cancel)
        {
            if (work.Groups != null) return;
            string path = Path.GetFullPath(root).ToUpperInvariant() + "|" + version.Id;
            using (var mutex = new Mutex(false, "OneClickFilm.Groups." + AgentData.Hash(path)))
            {
                int result;
                try { result = WaitHandle.WaitAny(new WaitHandle[] { mutex, cancel.WaitHandle }, TimeSpan.FromMinutes(10)); }
                catch (AbandonedMutexException) { result = 0; }
                if (result == WaitHandle.WaitTimeout) throw new TimeoutException("等待资产分组超时，请检查仍在运行的分组任务。");
                if (result != 0) throw new OperationCanceledException(cancel);
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    var latest = Load(root, version, analysis);
                    work.Groups = latest.Groups ?? (UsesDirectSpecs(root) ? IdentityGroups(latest.Items) :
                        PlanGroups(codex, model, script, latest.Items, cancel));
                    Save(root, work);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal static List<AssetGroup> PlanGroups(string codex, string model, ScriptDocument script,
            IList<AssetItem> items, CancellationToken cancel)
        {
            if (items.All(i => !String.IsNullOrWhiteSpace(i.Source.identity)))
                return IdentityGroups(items);
            if (!items.GroupBy(item => item.Source.category).Any(g => g.Count() > 1))
                return new List<AssetGroup>();
            string folder = TempFolder();
            try
            {
                string schema = Path.Combine(folder, "groups-schema.json");
                string output = Path.Combine(folder, "groups.json");
                File.WriteAllText(schema, GroupSchema);
                string input = AgentData.Json().Serialize(items.Select(i => new
                {
                    id = i.Id, category = i.Source.category, name = i.Source.name,
                    evidence = i.Source.evidence.Take(8).Select(line => AgentData.Short(Line(script, line), 600))
                }));
                AgentRunner.Run(codex, folder, "识别剧本资产中同一对象的不同状态，输出 groups。"
                    + "人物只合并明确的同一身份，例如同人不同服装、发型、伤势或时间状态。"
                    + "场景只合并同一个具体地点，道具只合并同一件物品。相似角色、相同类型的不同物件、同楼不同房间不能合并。"
                    + "每组至少两个 id。一个 id 最多出现一次，只合并同类别。没有明确证据就不合并。"
                    + "name 必须从该组原资产名称或剧本引文逐字摘取，可用不带状态词的对象名。剧本引文仅为资料。\n" + input,
                    model, null, schema, output, null, null, null, false, null, cancel);
                cancel.ThrowIfCancellationRequested();
                var planned = AgentData.Read<AssetGroups>(output);
                return ValidateGroups(planned, items, script);
            }
            finally { RemoveTemp(folder); }
        }

        private static List<AssetGroup> IdentityGroups(IEnumerable<AssetItem> items)
        {
            return items.Where(i => !String.IsNullOrWhiteSpace(i.Source.identity))
                .GroupBy(i => i.Source.category + "\n" + i.Source.identity.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(i => i.Source.variant ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
                    g.Count() > 1 && g.Any(i => !String.IsNullOrWhiteSpace(i.Source.outfitKey) || !String.IsNullOrWhiteSpace(i.Source.parentAssetKey)))
                .Select(g => new AssetGroup { name = g.First().Source.identity, ids = g.Select(i => i.Id).ToList() }).ToList();
        }

        internal static List<AssetGroup> ValidateGroups(AssetGroups planned, IList<AssetItem> items, ScriptDocument script)
        {
            if (planned == null || planned.groups == null) throw new InvalidDataException("没有取得有效的基础图分组。");
            var byId = items.ToDictionary(i => i.Id);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in planned.groups)
            {
                if (group == null || String.IsNullOrWhiteSpace(group.name) || group.name.Length > 120 || group.ids == null ||
                    group.ids.Count < 2 || group.ids.Any(id => id == null || !byId.ContainsKey(id) || !assigned.Add(id)))
                    throw new InvalidDataException("基础图分组包含无效、重复或未知资产。");
                var members = group.ids.Select(id => byId[id]).ToList();
                if (members.Select(m => m.Source.category).Distinct().Count() != 1 ||
                    !members.Any(m => m.Source.name.Contains(group.name) ||
                        m.Source.evidence.Any(line => Line(script, line).Contains(group.name))))
                    throw new InvalidDataException("基础图分组的类别或对象名无法从原文核实。");
            }
            return planned.groups;
        }

        private static readonly Regex VariantSuffix = new Regex(
            @"^(?<name>.+?)(?:\s*[·・/－—-]\s*|\s*[（(]\s*)(?<state>完好|完整|干净|常态|沾血|染血|破损|破碎|淋湿|湿透|污渍|脱外套|脱去外套|脱下外套|打开|闭合|关闭)\s*[）)]?$",
            RegexOptions.CultureInvariant);

        private static string VariantFamily(AssetItem item)
        {
            string variant = (item.Source.variant ?? "").Trim();
            var match = VariantSuffix.Match(variant);
            return match.Success ? match.Groups["name"].Value.Trim() : variant;
        }

        private static bool CleanVariant(AssetItem item)
        {
            const string changed = @"(?:沾血|染血|破损|破碎|淋湿|湿透|污渍|脱外套|脱去外套|脱下外套|打开|闭合|关闭)[）)]?$";
            return !Regex.IsMatch((item.Source.variant ?? "").Trim(), changed) &&
                !Regex.IsMatch((item.Source.episodeState ?? "").Trim(), changed);
        }

        // Resolve only within a single identity. An explicit link can name a concrete asset or its outfit.
        private static AssetItem ResolveParentItem(AssetItem item, List<AssetItem> siblings)
        {
            var a = item.Source;
            if (a.isBaseState) return null;
            string key = (a.parentAssetKey ?? "").Trim();
            if (key.Length > 0)
            {
                var direct = siblings.Where(i => i.Id != item.Id &&
                    (i.Id == key || i.Source.category + "|" + i.Source.identity + "|" + i.Source.variant == key ||
                    i.Source.name == key)).ToList();
                if (direct.Count == 1) return direct[0];
            }
            string outfit = key.Length > 0 ? key : (a.outfitKey ?? "").Trim();
            List<AssetItem> candidates;
            if (outfit.Length > 0)
            {
                candidates = siblings.Where(i => String.Equals((i.Source.outfitKey ?? "").Trim(), outfit, StringComparison.OrdinalIgnoreCase)).ToList();
                if (key.Length > 0 && candidates.Count == 0 && !String.IsNullOrWhiteSpace(a.outfitKey))
                    candidates = siblings.Where(i => i.Source.isBaseState && String.Equals((i.Source.outfitKey ?? "").Trim(), a.outfitKey.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                string family = VariantFamily(item);
                candidates = siblings.Where(i => String.IsNullOrWhiteSpace(i.Source.outfitKey) && VariantFamily(i) == family).ToList();
                if ((a.category == "道具" || a.category == "场景") && !candidates.Any(i => i.Source.isBaseState))
                {
                    var objectBases = siblings.Where(i => i.Source.isBaseState && String.IsNullOrWhiteSpace(i.Source.outfitKey)).ToList();
                    if (objectBases.Count == 1) return objectBases[0];
                }
                if (!candidates.Any(i => VariantSuffix.IsMatch((i.Source.variant ?? "").Trim()))) return null;
            }
            var roots = candidates.Where(i => String.IsNullOrWhiteSpace(i.Source.parentAssetKey) || i.Source.isBaseState).ToList();
            var marked = roots.Where(i => i.Source.isBaseState).ToList();
            if (marked.Count > 1) return null;
            var parent = marked.FirstOrDefault() ?? roots.FirstOrDefault(CleanVariant);
            // With an explicit shared outfit, even the earliest observed state may already be damaged.
            if (parent == null && outfit.Length > 0) parent = roots.FirstOrDefault();
            return parent == item ? null : parent;
        }

        internal static Dictionary<string, AssetItem> StateParents(AssetWork work)
        {
            var result = new Dictionary<string, AssetItem>();
            foreach (var group in work.Items.Where(i => !String.IsNullOrWhiteSpace(i.Source.identity))
                .GroupBy(i => i.Source.category + "\n" + i.Source.identity.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var siblings = group.ToList();
                foreach (var item in siblings)
                {
                    var basis = ResolveParentItem(item, siblings);
                    if (basis != null) result.Add(item.Id, basis);
                }
            }
            return result;
        }

        internal static string StateLabel(Dictionary<string, AssetItem> parents, AssetItem item)
        {
            AssetItem basis;
            if (parents.TryGetValue(item.Id, out basis)) return "派生自 · " + basis.Source.name;
            int count = parents.Values.Count(i => i.Id == item.Id);
            return count == 0 ? null : (item.Source.category == "人物" || item.Source.category == "角色" ? "服装" : item.Source.category)
                + "基准态 · 下挂 " + count + " 个状态变体";
        }

        internal static HashSet<string> ChildStates(AssetWork work, string id)
        {
            return new HashSet<string>(StateParents(work).Where(p => p.Value.Id == id).Select(p => p.Key)
                .Concat(work.Batches().SelectMany(p => p.Value.Jobs).Where(j => j.ParentItemId == id).Select(j => j.ItemId)));
        }

        private static bool UsableParent(AssetAttempt attempt)
        {
            return attempt != null && !String.IsNullOrEmpty(attempt.Path) && (attempt.State == "待审图" || attempt.State == "已通过");
        }

        internal static void InvalidateImageJobs(AssetWork work, Func<AssetImageJob,bool> affected, ISet<string> staleItems = null)
        {
            // Keep receipts for submissions whose outcome still needs reconciliation.
            foreach (var pair in work.Batches().Where(p=>p.Value.NeedsAction).ToList())
            {
                var jobs = pair.Value.Jobs.Where(affected).ToList();
                if (jobs.Any(j=>!j.Done && j.Attempt!=null && j.Attempt.State!="未提交"))
                    throw new InvalidOperationException("本组仍有等待回图的原任务，请先核对原任务再重做。");
                pair.Value.Jobs.RemoveAll(j => affected(j) && (j.IsBase || !j.Done || staleItems != null && staleItems.Contains(j.ItemId)));
                if (!pair.Value.Jobs.Any(j=>!j.IsBase)) work.StoreBatch(pair.Key,null);
            }
        }

        internal static void RejectImage(AssetWork work,string id,string expected,bool replace,string input,bool clearBase)
        {
            var item=work.Items.Single(i=>i.Id==id);
            var attempt=item.Active;
            if(attempt==null || attempt.Id!=expected || attempt.State!="待审图") throw new InvalidOperationException("图片已经变化，请刷新。");
            if(String.IsNullOrWhiteSpace(input)) throw new InvalidDataException(replace ? "请填写完整的新提示词。" : "请填写修改意见。");
            var children = ChildStates(work, id);
            var affectedGroup=(work.Groups ?? new List<AssetGroup>()).FirstOrDefault(g=>g.ids.Contains(id));
            InvalidateImageJobs(work, j => children.Contains(j.ItemId) || j.ItemId == id ||
                replace && clearBase && affectedGroup != null && j.GroupId == affectedGroup.Key, children);
            MarkDerivedImages(work, item, children, null);
            if(replace)
            {
                var group = affectedGroup;
                if(clearBase && group!=null && work.BaseImages!=null)work.BaseImages.Remove(group.Key);
                item.Prompt=input.Trim();item.PromptOrigin="人工重做";item.Conflict=null;item.PendingRepair=null;
            }
            attempt.ReviewNote=replace ? null : input.Trim();
            attempt.State="已退回";attempt.ReviewedUtc=AnalysisData.Now();
        }

        private static void MarkDerivedImages(AssetWork work, AssetItem basis, HashSet<string> children, AssetAttempt replacement)
        {
            foreach (var item in work.Items.Where(i => children.Contains(i.Id) && UsableParent(i.Active)))
            {
                var active = item.Active;
                if (replacement != null && active.BaseImageDigest == replacement.Digest) continue;
                active.State = "已退回";
                active.ReviewNote = "基准态「" + basis.Source.name + "」" + (replacement == null ? "已退回" : "已更新") + "，请使用新基准图重做此状态。";
                active.Detail = active.ReviewNote;
                active.ReviewedUtc = AnalysisData.Now();
                work.DeliveredId = null;
            }
        }

        internal static void ImportLocal(string root, AssetWork work,string id,string previous,string file,bool approved)
        {
            var item=work.Items.Single(i=>i.Id==id);
            if(item.ActiveId!=previous)throw new IOException("资产版本已变化，请刷新。");
            var attempt=new AssetAttempt {Id=AgentData.Id(),Channel="本地导入",PlatformId="local",
                Prompt=item.Prompt ?? "本地图片导入",Style=item.PromptStyle ?? work.Style,PromptLines=item.PromptLines,
                TargetSize=ImageOutputSettings.DefaultSize(null),CreatedUtc=AnalysisData.Now()};
            Attach(root,work,attempt,file);
            if (item.Active != null && item.Active.Path != null && item.Active.Digest != attempt.Digest)
            {
                var children = ChildStates(work, id);
                InvalidateImageJobs(work, j => children.Contains(j.ItemId), children);
                MarkDerivedImages(work, item, children, attempt);
            }
            if(approved){attempt.State="已通过";attempt.ReviewedUtc=AnalysisData.Now();attempt.ReviewNote="本地导入替换";}
            item.Attempts.Add(attempt);item.ActiveId=attempt.Id;
            foreach(var pair in work.Batches().Where(p=>p.Value.NeedsAction))
                foreach(var job in pair.Value.Jobs.Where(j=>j.ItemId==id && !j.Done))
                    job.Attempt=attempt;
        }

        private static string BuildBaseReferencePrompt(AssetGroup group, List<AssetItem> members, string style)
        {
            string category = members[0].Source.category;
            var sources = members.Select(i => i.Source).ToList();
            var parts = new List<string> { category + "「" + group.name + "」跨状态共用基础参考图", ImageLayout(category) };
            parts.Add(category == "场景" || category == "道具"
                ? "锁定共用空间布局或器物固定结构、材质与比例，互斥的光线、污损与开合状态留给分项变体。"
                : "角色身份共用基础五视图。锁定骨相、五官、发际线、体型与真实年龄肤质，着简素中性基础服装。各套具体服装及湿透、沾血、破损等状态留给下游分项变体。");
            var identityAnchors = sources.Where(a => String.IsNullOrWhiteSpace(a.variant) && !String.IsNullOrWhiteSpace(a.appearance)).ToList();
            var singleAnchor = identityAnchors.Count == 1 && sources.Any(a => !String.IsNullOrWhiteSpace(a.variant)) ? identityAnchors[0] : null;
            var traits = sources.Select(a => Regex.Split(a.appearance ?? "", @"[，,；;。\r\n]+")
                .Select(t => t.Trim()).Where(t => t.Length > 0))
                .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sharedLocks = sources.Select(a => (IEnumerable<string>)(a.continuityLocks ?? new List<string>()))
                .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase))
                .Where(t => !String.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (singleAnchor != null)
            {
                if (!String.IsNullOrWhiteSpace(singleAnchor.promptName)) parts.Add("提示词名称：" + singleAnchor.promptName.Trim());
                parts.Add("本体外观特征：" + singleAnchor.appearance.Trim());
                var extraTraits = traits.Where(t => singleAnchor.appearance.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0).ToList();
                if (extraTraits.Count > 0) parts.Add("共同外观特征：" + String.Join("；", extraTraits));
                sharedLocks = (singleAnchor.continuityLocks ?? new List<string>())
                    .Concat(sharedLocks).Where(t => !String.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (traits.Count > 0)
            {
                parts.Add("共同外观特征：" + String.Join("；", traits));
            }
            else
            {
                var primary = sources.FirstOrDefault(a => a.isBaseState && !String.IsNullOrWhiteSpace(a.appearance))
                    ?? sources.FirstOrDefault(a => !String.IsNullOrWhiteSpace(a.appearance));
                if (primary != null)
                    parts.Add((category == "场景" || category == "道具"
                        ? "空间/器物固定基准（仅提取其中的固定结构、比例与材质，忽略互斥光线或污损状态）："
                        : "身份与体貌基准（仅提取其中人物的性别、年龄、骨相、五官、发型、肤质与体型，严禁照搬其中具体服装或受伤/湿透状态）：")
                        + primary.appearance.Trim());
            }
            if (sharedLocks.Count > 0) parts.Add("共同连戏锁：" + String.Join("；", sharedLocks));
            if (category != "场景" && category != "道具")
                parts.Add("还原约束：真实还原年龄、骨相、肤质、体型与发型特征，严禁用泛化网红磨皮审美覆盖设定。");
            string eraText = sources.Select(a => (a.eraBackground ?? "").Trim()).FirstOrDefault(v => v.Length > 0);
            if (!String.IsNullOrWhiteSpace(eraText)) parts.Add("时代背景：" + eraText);
            string cleanedStyle = CleanVisualStyle(style);
            if (!String.IsNullOrWhiteSpace(cleanedStyle)) parts.Add("画面风格：" + cleanedStyle);
            var policies = sources.Select(a => a.propTextPolicy).Distinct().ToList();
            string policy = policies.Count == 1 ? policies[0] : null;
            if (category == "道具" && !String.IsNullOrWhiteSpace(policy)) parts.Add("共同字迹设定：" + policy);
            parts.Add(BuildTextPolicyRule(category, policy));
            return String.Join("\n", parts);
        }

        internal static AssetImageBatch NewBatch(string project, AssetWork work, ScriptDocument script,
            IList<AssetItem> selected, AssetChannel channel, string size, SkillReference imageSkill)
        {
            var result = new AssetImageBatch
            {
                Id = AgentData.Id(), ChannelId = channel == null ? "builtin" : channel.Id,
                ChannelName = channel == null ? "Codex 内置图片生成" : channel.Name,
                ChannelProfile = ChannelProfile(channel), ReferencePath = work.ReferencePath,
                ReferenceDigest = work.ReferenceDigest, Jobs = new List<AssetImageJob>()
            };
            result.TargetSize = size;
            result.ImageSkill = imageSkill;
            var parents = StateParents(work);
            var requested = new List<AssetItem>(selected);
            var reused = new Dictionary<string, AssetAttempt>();
            foreach (var item in selected)
            {
                AssetItem parent;
                if (!parents.TryGetValue(item.Id, out parent))
                {
                    if (!item.Source.isBaseState && !String.IsNullOrWhiteSpace(item.Source.parentAssetKey))
                        throw new InvalidDataException(item.Source.name + " 的基准态未能唯一匹配：" + item.Source.parentAssetKey);
                    continue;
                }
                if (parents.ContainsKey(parent.Id) || (!parent.Source.isBaseState && !String.IsNullOrWhiteSpace(parent.Source.parentAssetKey)))
                    throw new InvalidDataException(item.Source.name + " 的父项也是状态变体，请指定独立基准态。");
                if (requested.Any(i => i.Id == parent.Id)) continue;
                if (UsableParent(parent.Active))
                {
                    Verify(project, parent.Active);
                    reused[parent.Id] = parent.Active;
                }
                else if (PromptReady(parent)) requested.Add(parent);
                else throw new InvalidDataException("请先补齐基准态「" + parent.Source.name + "」的出图描述。");
            }
            var groups = work.Groups ?? IdentityGroups(work.Items);
            foreach (var group in groups.Where(g => requested.Any(i => g.ids.Contains(i.Id))))
            {
                var members = work.Items.Where(i => group.ids.Contains(i.Id)).ToList();
                if (members.Count(i => !parents.ContainsKey(i.Id)) < 2) continue;
                var first = members[0];
                var citations = members.SelectMany(i => i.Source.evidence).Distinct().OrderBy(n => n).ToList();
                var job = new AssetImageJob { GroupId = group.Key, Name = group.name + " · 基础图", Category = first.Source.category,
                    Lines = citations, Style = work.Style, DirectSpec = members.All(i => i.PromptOrigin == DirectPromptOrigin),
                    Prompt = BuildBaseReferencePrompt(group, members, work.Style) };
                job.BaseRevision = AgentData.Hash(AgentData.Json().Serialize(new { job.Prompt, work.ReferenceDigest,
                    result.ChannelProfile, size, Skill = imageSkill == null ? null : imageSkill.Digest }));
                AssetAttempt cached;
                if (work.BaseImages != null && work.BaseImages.TryGetValue(group.Key, out cached) &&
                    cached != null && cached.State == "已通过" && cached.BaseRevision == job.BaseRevision)
                {
                    try { Verify(project, cached); job.Attempt = cached; job.ReusedBase = true; }
                    catch (IOException) { }
                    catch (InvalidDataException) { }
                }
                if (job.ReusedBase || requested.Any(i => group.ids.Contains(i.Id) && !parents.ContainsKey(i.Id)))
                    result.Jobs.Add(job);
            }

            foreach (var item in requested)
            {
                var group = groups.FirstOrDefault(g => g.ids.Contains(item.Id));
                AssetItem parent;
                parents.TryGetValue(item.Id, out parent);
                AssetAttempt existingParent = null;
                if (parent != null) reused.TryGetValue(parent.Id, out existingParent);
                var unresolved = item.Active;
                var job = new AssetImageJob { ItemId = item.Id, GroupId = group != null && result.Jobs.Any(j => j.IsBase && j.GroupId == group.Key) ? group.Key : null,
                    ParentItemId = parent == null ? null : parent.Id, ExistingParent = existingParent,
                    OutfitKey = item.Source.outfitKey, Tier = parent == null ? 1 : 2, Category = item.Source.category,
                    DirectSpec = item.PromptOrigin == DirectPromptOrigin,
                    Name = item.Source.name, Prompt = item.Prompt, Style = item.PromptStyle ?? work.Style,
                    Lines = item.PromptLines, PromptSkill = item.PromptSkill };
                if (unresolved != null && unresolved.State == "已退回" && !String.IsNullOrWhiteSpace(unresolved.ReviewNote) &&
                    !unresolved.ReviewNote.StartsWith("基准态「", StringComparison.Ordinal))
                    job.Prompt += "\n上次审核的修改要求：" + unresolved.ReviewNote;
                if (unresolved != null && (unresolved.State == "需核对" || unresolved.State == "运行中" || unresolved.State == "已暂停"))
                    job.Attempt = unresolved;
                result.Jobs.Add(job);
            }
            return result;
        }

        internal static string ChannelProfile(AssetChannel channel)
        {
            if (FlowChannels.IsResponsesApi(channel) || channel == null && CodexAuthMode.IsApi)
                return "responses-" + AgentData.Hash(CodexAuthMode.BaseUrl + "\n" + CodexAuthMode.Model);
            if (channel == null) return "builtin";
            return AgentData.Hash(AgentData.Json().Serialize(
                new { channel.Id, channel.Executable, channel.Arguments, channel.CheckArguments }));
        }

        internal static string BatchReference(string project, AssetImageBatch batch)
        {
            if (batch.ReferencePath == null) return null;
            return Verify(project, new AssetAttempt { Path = batch.ReferencePath, Digest = batch.ReferenceDigest });
        }

        internal static IEnumerable<AssetImageJob> ReadyJobs(AssetImageBatch batch)
        {
            return batch.Jobs.Where(j => j.Attempt == null && (j.IsBase || ReadyForReference(batch, j)));
        }

        internal static AssetAttempt JobBasis(AssetImageBatch batch, AssetImageJob job)
        {
            if (job.IsBase) return null;
            if (!String.IsNullOrEmpty(job.ParentItemId))
            {
                var parent = batch.Jobs.FirstOrDefault(j => j.ItemId == job.ParentItemId);
                return parent == null ? job.ExistingParent : parent.Attempt;
            }
            var face = batch.Jobs.FirstOrDefault(j => j.IsBase && j.GroupId == job.GroupId);
            return face == null ? null : face.Attempt;
        }

        private static bool ReadyForReference(AssetImageBatch batch, AssetImageJob job)
        {
            if (!String.IsNullOrEmpty(job.ParentItemId))
            {
                if (!UsableParent(JobBasis(batch, job))) return false;
                // Native state jobs also attach the shared reference, even with a previously saved parent.
                if (!FlowChannels.IsNative(batch.ChannelId)) return true;
            }
            var face = batch.Jobs.FirstOrDefault(j => j.IsBase && j.GroupId == job.GroupId);
            return face == null || face.Done && face.Attempt.State == "已通过";
        }

        internal static void ReviewBase(string project, AssetWork work, string batchId, string attemptId, bool accept)
        {
            ReviewBase(project, work, batchId, attemptId, accept, false, "按原方案重新制作基础图。");
        }

        internal static void ReviewBase(string project, AssetWork work, string batchId, string attemptId, bool accept,
            bool replacePrompt, string feedback)
        {
            var batch = work.Batches().Select(p => p.Value).FirstOrDefault(b => b.Id == batchId);
            if (batch == null) throw new InvalidOperationException("批次已变化，请刷新。");
            var job = batch.Jobs.SingleOrDefault(j => j.IsBase && j.Attempt != null && j.Attempt.Id == attemptId);
            if (job == null || job.Attempt.State != "待审图") throw new InvalidOperationException("基础图审核状态已变化。");
            if (accept) Verify(project, job.Attempt);
            else
            {
                if (String.IsNullOrWhiteSpace(feedback))
                    throw new InvalidDataException(replacePrompt ? "请填写完整的新提示词。" : "请填写修改意见。");
                string value = feedback.Trim();
                job.Attempt.ReviewNote = (replacePrompt ? "全新提示词 · " : "修改意见 · ") + value;
                job.Prompt = replacePrompt ? value : job.Prompt + "\n基础图修改要求：" + value;
                job.ApprovedPrompt = null;
            }
            job.Attempt.ReviewedUtc = AnalysisData.Now();
            job.Attempt.State = accept ? "已通过" : "已退回";
            if (work.BaseImages == null) work.BaseImages = new Dictionary<string, AssetAttempt>();
            work.BaseImages[job.GroupId] = job.Attempt;
            if (!accept) { job.ReusedBase = false; ResetJob(work, job); }
        }

        internal static void AcceptFlowBase(string project, AssetWork work, AssetImageBatch batch, AssetImageJob job)
        {
            ReviewBase(project, work, batch.Id, job.Attempt.Id, true);
            job.Attempt.ReviewNote = "一键成片 · 已校验基础图，可生成关联资产";
        }

        internal static void ApplyImageJob(AssetWork work, AssetImageJob job)
        {
            if (job.Attempt == null) return;
            if (job.IsBase)
            {
                if (job.Done)
                {
                    if (work.BaseImages == null) work.BaseImages = new Dictionary<string, AssetAttempt>();
                    work.BaseImages[job.GroupId] = job.Attempt;
                }
                return;
            }
            var item = work.Items.Single(i => i.Id == job.ItemId);
            if (job.Done && item.Attempts.Any(a => a.Id != job.Attempt.Id && a.Path != null && a.Digest != job.Attempt.Digest))
                MarkDerivedImages(work, item, ChildStates(work, item.Id), job.Attempt);
            int index = item.Attempts.FindIndex(a => a.Id == job.Attempt.Id);
            if (index < 0) item.Attempts.Add(job.Attempt);
            else item.Attempts[index] = job.Attempt;
            item.ActiveId = job.Attempt.Id;
        }

        internal static void ResetJob(AssetWork work, AssetImageJob job)
        {
            if (job.Attempt == null) return;
            var previous = job.Attempt;
            if (previous.State == "已退回") job.RetrySource = null;
            else if (String.IsNullOrEmpty(previous.Path))
            {
                job.RetrySource = previous;
                previous.State = previous.State == "需核对" ? previous.State : "已暂停";
                ApplyImageJob(work, job);
            }
            if (job.IsBase) RememberBase(work, job.GroupId, job, previous, true);
            if (!job.IsBase)
            {
                var item = work.Items.Single(i => i.Id == job.ItemId);
                if (item.ActiveId == previous.Id)
                    item.ActiveId = item.Attempts.Where(a => a.Path != null).Select(a => a.Id).LastOrDefault();
            }
            job.Attempt = null;
        }

        private static void RememberBase(AssetWork work, string group, AssetImageJob job, AssetAttempt attempt, bool update = false)
        {
            if (work.BaseHistory == null) work.BaseHistory = new Dictionary<string, List<AssetAttempt>>();
            List<AssetAttempt> history;
            if (!work.BaseHistory.TryGetValue(group, out history) || history == null)
                work.BaseHistory[group] = history = new List<AssetAttempt>();
            StoreBaseAttempt(history, attempt, update);
            if (job == null) return;
            if (job.EarlierBaseAttempts == null) job.EarlierBaseAttempts = new List<AssetAttempt>();
            StoreBaseAttempt(job.EarlierBaseAttempts, attempt, update);
        }

        private static void StoreBaseAttempt(List<AssetAttempt> history, AssetAttempt attempt, bool update)
        {
            int index = history.FindIndex(a => a.Id == attempt.Id);
            if (index < 0) history.Add(attempt);
            else if (update) history[index] = attempt;
        }

        private static void RestoreBaseHistory(AssetWork work)
        {
            // Older projects kept the last rejection in the group cache and interrupted attempts on the batch.
            var bases = work.Batches().SelectMany(p => p.Value.Jobs).Where(j => j.IsBase).ToList();
            foreach (var job in bases.Where(j => j.EarlierBaseAttempts != null))
                foreach (var attempt in job.EarlierBaseAttempts) RememberBase(work, job.GroupId, null, attempt);
            if (work.BaseImages == null) return;
            foreach (var pair in work.BaseImages.Where(p => p.Value != null && p.Value.State == "已退回"))
            {
                var owner = bases.FirstOrDefault(j => j.GroupId == pair.Key && j.Attempt == null && j.BaseRevision == pair.Value.BaseRevision);
                RememberBase(work, pair.Key, owner, pair.Value);
            }
        }

        internal static void ReconcileImages(string root, AssetWork work, CancellationToken cancel)
        {
            ReconcileBatch(root, work, work.ImageBatch, cancel);
        }

        internal static void ReconcileBatch(string root, AssetWork work, AssetImageBatch batch, CancellationToken cancel)
        {
            foreach (var job in batch.Jobs.Where(j => !j.Done && j.Attempt != null))
            {
                cancel.ThrowIfCancellationRequested();
                var current = job.Attempt;
                var history = job.IsBase ? job.EarlierBaseAttempts : work.Items.Single(i => i.Id == job.ItemId).Attempts;
                var candidates = (history ?? new List<AssetAttempt>()).AsEnumerable().Reverse()
                    .Where(a => a.Id != current.Id && a.State != "已退回" && a.Path == null && a.Prompt == current.Prompt &&
                        a.PlatformId == current.PlatformId && a.Style == current.Style && a.TargetSize == current.TargetSize &&
                        a.ReferenceDigest == current.ReferenceDigest && a.BaseImageDigest == current.BaseImageDigest &&
                        a.BaseRevision == current.BaseRevision && (a.ImageSkill == null ? null : a.ImageSkill.Digest) ==
                        (current.ImageSkill == null ? null : current.ImageSkill.Digest));
                string reason = null;
                foreach (var candidate in new[] { current }.Concat(candidates))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        if (!TryRecoverImage(root, work, candidate)) continue;
                        job.Attempt = candidate;
                        break;
                    }
                    catch (Exception e) { reason = AgentData.Short(e.Message, 190); }
                }
                if (job.Done && !String.IsNullOrEmpty(job.ImageIncidentId))
                    FlowIncidentStore.ResolveIncident(root, FlowHalf.PreAssets, FlowTelemetry.StageAssets, job.Name,
                        "核对原任务后收到回图。", 0, job.RetryCount, job.ImageIncidentId);
                if (!job.Done)
                {
                    string folder = AgentMedia.JobFolder(root, new AgentJob { Id = current.Id });
                    bool invoked = File.Exists(Path.Combine(folder, "invoked"));
                    if (AgentMedia.ConfirmedFailure(folder))
                    {
                        current.State = "已暂停";
                        ApplyImageJob(work, job);
                        Save(root, work);
                        continue;
                    }
                    if (current.State == "未提交" && (current.Detail ?? "").StartsWith("人工核对")) continue;
                    current.State = invoked || current.State == "需核对" ? "需核对" : "已暂停";
                    const string check = "已检查原任务，尚无可验证的回图。";
                    var original = current.Detail ?? "";
                    if (reason != null) current.Detail = reason;
                    else if (!original.Contains(check)) current.Detail = original + " " + check;
                }
                ApplyImageJob(work, job);
                Save(root, work);
            }
        }

        private static bool RejectedBase(AssetImageJob job)
        {
            return job.IsBase && job.Attempt == null && job.EarlierBaseAttempts != null && job.EarlierBaseAttempts.Any(a => a.State == "已退回");
        }

        internal static string ResumeLabel(AssetImageBatch batch)
        {
            var ready = ReadyJobs(batch).ToList();
            int bases = ready.Count(j => j.IsBase), assets = ready.Count - bases;
            if (assets == 0) return (ready.Any(RejectedBase) ? "重新生成基础图" : "生成基础图") + "（" + bases + " 张）";
            if (bases != 0) return "继续生成基础图 " + bases + " 张与造型 " + assets + " 项";
            return (ready.All(j => j.GroupId != null) ? "继续生成同组造型" : "继续生成待出图资产") + "（" + assets + " 项）";
        }

        internal static string PendingSummary(AssetImageBatch batch)
        {
            int waiting = batch.Jobs.Count(j => j.IsBase && j.Done && j.Attempt.State == "待审图");
            if (waiting != 0) return "基础图待审核（" + waiting + " 张）。采用后可继续生成关联资产。";
            var rejected = batch.Jobs.Where(RejectedBase).Select(j => j.GroupId).ToList();
            if (rejected.Count != 0) return "基础图已退回（" + rejected.Count + " 张），等待重新生成；同组 " +
                batch.Jobs.Count(j => !j.IsBase && !j.Done && rejected.Contains(j.GroupId)) + " 项资产暂缓出图。";
            var stopped = batch.Jobs.Where(j => !j.Done && j.Attempt != null).ToList();
            if (stopped.Count > 0)
                return "已保存 " + batch.Jobs.Count(j => j.Done) + " / " + batch.Jobs.Count + " 张。"
                    + String.Join("；", stopped.Take(3).Select(j => j.Name + " · " + j.Attempt.State +
                        " · " + AgentData.Short(j.Attempt.Detail ?? "等待核对原任务", 90))) +
                    (stopped.Count > 3 ? "（另有 " + (stopped.Count - 3) + " 项）" : "");
            if (!batch.NeedsAction) return "出图完成，请逐项审图。";
            var ready = ReadyJobs(batch).ToList();
            if (ready.Count > 0 && ready.All(j => !j.IsBase && j.GroupId != null))
                return "基础图已采用，可继续生成待出图造型（" + ready.Count + " 项）。";
            return ready.Count == 0 ? "请核对基础图及关联任务状态。" : ResumeLabel(batch) + "。";
        }

        internal static bool CanResumeTimeout(string project, AssetImageBatch batch, AssetImageJob job)
        {
            return batch.AllowBuiltinTimeoutRetry && job.RetryCount < ImageOutputSettings.MaxRetryCount &&
                IsUnreturnedTimeout(project, batch, job);
        }

        internal static IEnumerable<AssetImageJob> TimedOutJobs(string project, AssetImageBatch batch)
        {
            if (batch == null || batch.Jobs == null) return Enumerable.Empty<AssetImageJob>();
            return batch.Jobs.Where(j => IsUnreturnedTimeout(project, batch, j));
        }

        private static bool IsUnreturnedTimeout(string project, AssetImageBatch batch, AssetImageJob job)
        {
            var a = job.Attempt;
            bool apiProfile = (batch.ChannelProfile ?? "").StartsWith("responses-", StringComparison.Ordinal);
            return FlowChannels.IsNative(batch.ChannelId) &&
                !job.Done && a != null && a.PlatformId == batch.ChannelId && a.State == "需核对" &&
                (a.TimedOut || (a.Detail ?? "").StartsWith("单张图片生成超时。", StringComparison.Ordinal)) &&
                (apiProfile ? UnsentApiImage(AgentMedia.JobFolder(project, new AgentJob { Id = a.Id })) : EmptyBuiltinSession(project, a));
        }

        private static bool UnsentApiImage(string folder)
        {
            // PerformImage saves the prompt before RunMedia exclusively creates "invoked" and sends.
            return File.Exists(Path.Combine(folder, AgentMedia.SubmittedPromptFile)) &&
                !File.Exists(Path.Combine(folder, "invoked")) && !File.Exists(Path.Combine(folder, AgentMedia.ReceiptFile)) &&
                !File.Exists(Path.Combine(folder, NativeImageRetry.ToolReceipt)) && Directory.GetFiles(folder, "native-image.*").Length == 0;
        }

        internal static bool EmptyBuiltinSession(string project, AssetAttempt attempt)
        {
            var folder = AgentMedia.JobFolder(project, new AgentJob { Id = attempt.Id });
            if (FlowChannels.IsResponsesApi(attempt.PlatformId) || CodexAuthMode.IsApi) return UnsentApiImage(folder);
            string session = Path.Combine(folder, AgentMedia.BuiltinSessionFile);
            if (!File.Exists(session) || File.Exists(Path.Combine(folder, AgentMedia.ReceiptFile))) return false;
            string id = File.ReadAllText(session).Trim();
            Guid parsed;
            if (!Guid.TryParse(id, out parsed) || AgentMedia.BuiltinCandidates(id).Length != 0 ||
                Directory.GetFiles(folder, "native-image.*").Length != 0) return false;
            string file = Path.Combine(folder, NativeImageRetry.ToolReceipt);
            if (!File.Exists(file)) return true;
            try
            {
                var tool = AgentData.Read<Dictionary<string, object>>(file);
                return tool != null && AgentData.Str(tool, "threadId") == id &&
                    Guid.TryParse(AgentData.Str(tool, "turnId"), out parsed) && AgentData.Str(tool, "itemId").Length > 0 &&
                    Object.Equals(AgentData.Value(tool, "terminalEvent"), false) && AgentData.Str(tool, "status") == "in_progress" &&
                    AgentData.Str(tool, "generationId").Length == 0 && AgentData.Str(tool, "fileId").Length == 0 &&
                    AgentData.Str(tool, "failure").Length == 0;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static bool TryRecoverImage(string project, AssetWork work, AssetAttempt attempt)
        {
            return TryRecoverImageWithCancellation(project, work, attempt, CancellationToken.None);
        }

        internal static bool TryRecoverImageWithCancellation(string project, AssetWork work, AssetAttempt attempt, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var folder = AgentMedia.JobFolder(project, new AgentJob { Id = attempt.Id });
            var receipt = AgentMedia.ReadJobReceipt(folder, null);
            if (attempt.PlatformId == FlowChannels.Dreamina && receipt != null && receipt.ExternalId != null &&
                receipt.State != "已取回" && receipt.State != "平台已失败")
            {
                var channel = FlowChannels.Channel(attempt.PlatformId);
                FlowChannels.QueryDreamina(channel.Executable, folder, receipt, cancel);
            }
            string path = receipt != null && receipt.State == "已取回" ? AgentData.Inside(folder, receipt.Output) : null;
            if (path == null && !FlowChannels.IsNative(attempt.PlatformId) && attempt.PlatformId != FlowChannels.Dreamina &&
                receipt != null && receipt.State == "已发起" && File.Exists(Path.Combine(folder, "invoked")))
            {
                string output = Path.Combine(folder, "output.png");
                if (File.Exists(output))
                {
                    // A CLI may have finished writing before Lacuna received its exit notification.
                    // Only claim a complete image after the submitting process releases the dispatch lock.
                    FileStream dispatch;
                    try { dispatch = new FileStream(Path.Combine(folder, "dispatch.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                    catch (IOException error) { if ((error.HResult & 0xffff) == 32) return false; throw; }
                    using (dispatch)
                    {
                        Inspect(output, new AssetAttempt());
                        receipt.Output = "output.png";
                        receipt.State = "已取回";
                        AgentData.Save(Path.Combine(folder, AgentMedia.ReceiptFile), receipt);
                        path = output;
                    }
                }
            }
            if (path == null && FlowChannels.IsNative(attempt.PlatformId))
            {
                if (Directory.Exists(folder))
                {
                    var nativeFiles = Directory.GetFiles(folder, "native-image.*");
                    if (nativeFiles.Length == 1) path = nativeFiles[0];
                    else if (nativeFiles.Length > 1) throw new InvalidDataException("同一任务目录存在多张本地回图，请核对原图。");
                }
                string sessionFile = Path.Combine(folder, AgentMedia.BuiltinSessionFile);
                if (path == null && !FlowChannels.IsResponsesApi(attempt.PlatformId) && File.Exists(sessionFile))
                {
                    var candidates = AgentMedia.BuiltinCandidates(File.ReadAllText(sessionFile).Trim());
                    if (candidates.Length > 1) throw new InvalidDataException("同一会话有多张候选图，请核对原图。");
                    path = candidates.SingleOrDefault();
                }
            }
            if (path == null) return false;
            string submitted = Path.Combine(folder, AgentMedia.SubmittedPromptFile);
            if (File.Exists(submitted)) attempt.Prompt = File.ReadAllText(submitted);
            Attach(project, work, attempt, path);
            return true;
        }
    }
}
