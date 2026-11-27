using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OneClickFilm
{
    public sealed class DirectorSceneNote
    {
        public string episode { get; set; }
        public string sceneCode { get; set; }
        public string sceneHeading { get; set; }
        public int first { get; set; }
        public int last { get; set; }
        public string beats { get; set; }
        public string plotCore { get; set; }
        public string visualFocus { get; set; }
        public List<string> requiredShots { get; set; }
        public string emotionFocus { get; set; }
        public string costume { get; set; }
        public string atmosphere { get; set; }
        public string keyProps { get; set; }
        public string specialNotes { get; set; }
    }

    public sealed class DirectorAnalysisDoc
    {
        public string Title { get; set; }
        public string VersionId { get; set; }
        public List<DirectorSceneNote> Scenes { get; set; }
        public List<string> Doubts { get; set; }
    }

    public sealed class VisualSettingEntry
    {
        public string kind { get; set; }
        public string name { get; set; }
        public string identity { get; set; }
        public string outfitKey { get; set; }
        public string stateName { get; set; }
        public bool isOutfitBase { get; set; }
        public string parentOutfitKey { get; set; }
        public string appearance { get; set; }
        public string episodeState { get; set; }
        public string voiceDirection { get; set; }
        public string promptName { get; set; }
        public string propTextPolicy { get; set; }
        public List<string> continuityLocks { get; set; }
        public List<int> evidence { get; set; }
    }

    public sealed class VisualSettingDoc
    {
        public string VersionId { get; set; }
        public string ProductionFormat { get; set; }
        public string EraBackground { get; set; }
        public List<VisualSettingEntry> Entries { get; set; }
        public string Markdown { get; set; }
        public List<string> PendingDecisions { get; set; }
    }

    internal static class DirectorPipeline
    {
        internal const string DirectorSchema = @"{""type"":""object"",""additionalProperties"":false,
            ""required"":[""scenes"",""directorScenes"",""doubts""],
            ""properties"":{
                ""scenes"":{""type"":""array"",""items"":{""type"":""object"",""additionalProperties"":false,
                ""required"":[""kind"",""title"",""first"",""last"",""action""],
                ""properties"":{
                    ""kind"":{""type"":""string"",""enum"":[""story"",""background"",""silent_transition"",""vo_transition""]},
                    ""title"":{""type"":""string""},
                    ""first"":{""type"":""integer""},
                    ""last"":{""type"":""integer""},
                    ""action"":{""type"":""string""}
                }}},
                ""directorScenes"":{""type"":""array"",""items"":{""type"":""object"",""additionalProperties"":false,
                ""required"":[""episode"",""sceneCode"",""sceneHeading"",""first"",""last"",""beats"",""plotCore"",""visualFocus"",""requiredShots"",""emotionFocus"",""costume"",""atmosphere"",""keyProps"",""specialNotes""],
                ""properties"":{
                    ""episode"":{""type"":""string""},
                    ""sceneCode"":{""type"":""string""},
                    ""sceneHeading"":{""type"":""string""},
                    ""first"":{""type"":""integer""},
                    ""last"":{""type"":""integer""},
                    ""beats"":{""type"":""string""},
                    ""plotCore"":{""type"":""string""},
                    ""visualFocus"":{""type"":""string""},
                    ""requiredShots"":{""type"":""array"",""items"":{""type"":""string""}},
                    ""emotionFocus"":{""type"":""string""},
                    ""costume"":{""type"":""string""},
                    ""atmosphere"":{""type"":""string""},
                    ""keyProps"":{""type"":""string""},
                    ""specialNotes"":{""type"":""string""}
                }}},
                ""doubts"":{""type"":""array"",""items"":{""type"":""string""}}
            }}";
        internal const string VisualSchema = @"{""type"":""object"",""additionalProperties"":false,
            ""required"":[""productionFormat"",""eraBackground"",""entries"",""pendingDecisions""],
            ""properties"":{
                ""productionFormat"":{""type"":""string""},
                ""eraBackground"":{""type"":""string""},
                ""entries"":{""type"":""array"",""items"":{""type"":""object"",""additionalProperties"":false,
                ""required"":[""kind"",""name"",""identity"",""outfitKey"",""stateName"",""isOutfitBase"",""parentOutfitKey"",""appearance"",""episodeState"",""voiceDirection"",""promptName"",""propTextPolicy"",""continuityLocks"",""evidence""],
                ""properties"":{
                    ""kind"":{""type"":""string"",""enum"":[""人物"",""造型"",""地点"",""道具""]},
                    ""name"":{""type"":""string""},
                    ""identity"":{""type"":""string""},
                    ""outfitKey"":{""type"":""string""},
                    ""stateName"":{""type"":""string""},
                    ""isOutfitBase"":{""type"":""boolean""},
                    ""parentOutfitKey"":{""type"":""string""},
                    ""appearance"":{""type"":""string""},
                    ""episodeState"":{""type"":""string""},
                    ""voiceDirection"":{""type"":""string""},
                    ""promptName"":{""type"":""string""},
                    ""propTextPolicy"":{""type"":""string"",""enum"":["""",""字迹清晰可读"",""只留版式轮廓"",""表面无字"",""字句待创作者拍板"",""只在后期叠加""]},
                    ""continuityLocks"":{""type"":""array"",""items"":{""type"":""string""}},
                    ""evidence"":{""type"":""array"",""items"":{""type"":""integer""}}
                }}},
                ""pendingDecisions"":{""type"":""array"",""items"":{""type"":""string""}}
            }}";

        internal sealed class DirectorPart
        {
            public List<AnalysisScene> scenes { get; set; }
            public List<DirectorSceneNote> directorScenes { get; set; }
            public List<string> doubts { get; set; }
        }

        internal static string ProjectContext(AnalysisResult director)
        {
            // Fit a spread of source-ordered scenes even when the manuscript spans hundreds of episodes.
            var rows=director.Parts.SelectMany(p=>AgentData.ParseOutput<DirectorPart>(p.skillOutput).directorScenes).ToList();
            int count=Math.Min(64,rows.Count);
            var context=new List<string>();
            for (int i=0;i<count;i++)
            {
                var d=rows[(int)((long)i*(rows.Count-1)/Math.Max(1,count-1))];
                context.Add(AgentData.Short(d.episode+" "+d.sceneHeading+" · "+d.plotCore,100)+" · "+
                    AgentData.Short(String.Join(" / ",new[]{d.costume,d.keyProps,d.atmosphere}.Where(t=>!String.IsNullOrWhiteSpace(t))),110));
            }
            return String.Join("\n",context);
        }

        internal static AnalysisPart ParseDirector(string json)
        {
            var data=AgentData.ParseOutput<DirectorPart>(json);
            if (data.scenes == null || data.scenes.Any(s => s == null) || data.directorScenes == null)
                throw new InvalidDataException("缺少场次 scenes 或导演分析 directorScenes。");
            bool normalized = data.doubts == null;
            if (normalized) data.doubts = new List<string>();
            foreach (var note in data.directorScenes)
            {
                if (note == null) throw new InvalidDataException("directorScenes 包含空条目。");
                if (String.IsNullOrWhiteSpace(note.plotCore))
                    throw new InvalidDataException(Range(note.first, note.last) + " 缺少剧情核心 plotCore。");
                if (note.requiredShots == null) { note.requiredShots = new List<string>(); normalized = true; }
            }
            var expected = data.scenes.Where(s => s.kind == "story" || s.kind == "vo_transition")
                .Select(s => Range(s.first, s.last)).ToList();
            var actual = data.directorScenes.Select(d => Range(d.first, d.last)).ToList();
            var missing = expected.Except(actual).ToArray();
            var extra = actual.Except(expected).Concat(actual.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key)).ToArray();
            if (missing.Length + extra.Length != 0)
                throw new InvalidDataException("导演分析与正文场次不符。缺失：" + String.Join("、", missing) + "；多出或重复：" + String.Join("、", extra));
            return new AnalysisPart { scenes=data.scenes, summary=String.Join("\n",data.scenes.Select(s=>s.action)),
                assets=new List<AnalysisAsset>(), skillOutput=normalized ? AgentData.Json().Serialize(data) : json };
        }

        private static string Range(int first, int last)
        {
            return first == last ? "L" + first : "L" + first + "–" + last;
        }

        internal static HashSet<int> ShootableLines(AnalysisPart director)
        {
            return new HashSet<int>(director.scenes.Where(s => s.kind != "background" && s.kind != "silent_transition")
                .SelectMany(s => Enumerable.Range(s.first, s.last - s.first + 1)));
        }

        internal static string FormatLineRanges(IEnumerable<int> lines)
        {
            var sorted = lines.Distinct().OrderBy(n => n).ToArray();
            var ranges = new List<string>();
            for (int i = 0; i < sorted.Length; i++)
            {
                int first = sorted[i];
                while (i + 1 < sorted.Length && sorted[i + 1] == sorted[i] + 1) i++;
                ranges.Add(Range(first, sorted[i]));
            }
            return ranges.Count == 0 ? "无" : String.Join("、", ranges);
        }

        internal static AnalysisPart ParseVisual(string json, AnalysisPart director, IEnumerable<AnalysisAsset> prior = null, bool parallelWave = false)
        {
            return ParseVisualLines(json, director, prior, parallelWave, ShootableLines(director));
        }

        internal static AnalysisPart ParseVisualLines(string json, AnalysisPart director, IEnumerable<AnalysisAsset> prior,
            bool parallelWave, HashSet<int> allowed)
        {
            var partition = DiagnoseVisual(json, prior, parallelWave, allowed);
            if (partition.Failures.Count > 0)
                throw new InvalidDataException(String.Join("；", partition.Failures.Values) + "；本段允许可拍行号：" + FormatLineRanges(allowed));
            var doc = partition.Document;
            doc.Markdown = VisualMarkdown(doc);
            return new AnalysisPart { scenes=director.scenes, summary=director.summary, assets=partition.Passed.Values.ToList(),
                skillOutput=AgentData.Json().Serialize(doc) };
        }

        internal sealed class VisualPartition
        {
            internal VisualSettingDoc Document;
            internal SortedDictionary<int,AnalysisAsset> Passed;
            internal SortedDictionary<int,string> Failures;
        }

        internal static VisualPartition DiagnoseVisual(string json, IEnumerable<AnalysisAsset> prior, bool parallelWave, HashSet<int> allowed)
        {
            var doc=AgentData.ParseOutput<VisualSettingDoc>(json);
            if (doc.Entries==null || doc.PendingDecisions==null || String.IsNullOrWhiteSpace(doc.ProductionFormat) ||
                String.IsNullOrWhiteSpace(doc.EraBackground))
                throw new InvalidDataException("视觉设定缺少制作形态、时代背景、条目或待定事项。");
            var failures = new SortedDictionary<int,string>();
            var assets = new SortedDictionary<int,AnalysisAsset>();
            for (int i = 0; i < doc.Entries.Count; i++)
            {
                var entry = doc.Entries[i];
                string label = entry == null || String.IsNullOrWhiteSpace(entry.name) ? "第 " + (i+1) + " 项" : entry.name;
                if (entry == null) { failures.Add(i, label + " 缺少视觉条目。"); continue; }
                var original = entry.evidence ?? new List<int>();
                entry.evidence = original.Where(allowed.Contains).Distinct().OrderBy(n => n).ToList();
                if (entry.evidence.Count == 0)
                {
                    failures.Add(i, "条目「" + label + "」的原文依据（" + FormatLineRanges(original) + "）没有本段可拍正文行号。");
                    continue;
                }
                try
                {
                    NormalizeCharacter(entry);
                    if (entry.continuityLocks == null) entry.continuityLocks = new List<string>();
                    if (!entry.isOutfitBase && !String.IsNullOrWhiteSpace(entry.outfitKey) && String.IsNullOrWhiteSpace(entry.parentOutfitKey))
                        entry.parentOutfitKey = entry.outfitKey.Trim();
                    assets.Add(i, Map(entry, doc.EraBackground));
                }
                catch (InvalidDataException e) { failures.Add(i, label + "：" + e.Message + " 越界行号：" + FormatLineRanges(original.Except(allowed))); }
            }
            if (!parallelWave)
            {
                var available = (prior ?? Enumerable.Empty<AnalysisAsset>()).Concat(assets.Values).ToList();
                foreach (var item in assets.Where(pair => !pair.Value.isBaseState && !Parents(pair.Value, available).Any()).ToList())
                {
                    failures.Add(item.Key, ParentDiagnostic(item.Value));
                    assets.Remove(item.Key);
                }
            }
            return new VisualPartition { Document=doc, Passed=assets, Failures=failures };
        }

        internal static string VisualPatchSchema()
        {
            var schema = AgentData.Json().Deserialize<Dictionary<string,object>>(VisualSchema);
            var properties = (Dictionary<string,object>)schema["properties"];
            schema["properties"] = new { entries = properties["entries"] };
            schema["required"] = new[] { "entries" };
            return AgentData.Json().Serialize(schema);
        }

        internal static string ParentDiagnostic(AnalysisAsset variant)
        {
            return variant.name + " 的基准态未能唯一匹配，identity=" + variant.identity + "，outfitKey=" +
                variant.outfitKey + "，parentAssetKey=" + variant.parentAssetKey;
        }

        private static IEnumerable<string> ExpandAliasToken(string token)
        {
            string trimmed = (token ?? "").Trim();
            if (trimmed.Length == 0) yield break;
            yield return trimmed;
            int open = trimmed.IndexOfAny(new[] { '（', '(' });
            if (open > 0)
            {
                string outer = trimmed.Substring(0, open).Trim();
                if (outer.Length > 0) yield return outer;
                int close = trimmed.IndexOfAny(new[] { '）', ')' }, open + 1);
                if (close > open + 1)
                {
                    string inner = trimmed.Substring(open + 1, close - open - 1).Trim();
                    if (inner.Length > 0) yield return inner;
                }
            }
        }

        private static string[] IdentityAliases(string identity)
        {
            string trimmed = (identity ?? "").Trim();
            if (trimmed.Length == 0) return new string[0];
            var list = new List<string>();
            var splits = new[] { trimmed }
                .Concat(trimmed.Split(new[] { '／', '/', '|', '｜' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
            foreach (string part in splits)
            {
                foreach (string item in ExpandAliasToken(part))
                {
                    list.Add(item);
                    int sep = item.IndexOfAny(new[] { '·', '•', '・', ' ' });
                    if (sep >= 2)
                    {
                        string given = item.Substring(0, sep).Trim();
                        if (given.Length >= 2) list.Add(given);
                    }
                }
            }
            return list.Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static string NormalizeOutfitKey(string identity, string rawKey)
        {
            string id = (identity ?? "").Trim();
            string key = (rawKey ?? "").Trim();
            if (id.Length == 0 || key.Length == 0) return key;
            string prefix = id + "::";
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length) return key;
            var aliases = IdentityAliases(id);
            string normalizedSep = key.Replace("：：", "::");
            int sep = normalizedSep.IndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                string left = normalizedSep.Substring(0, sep).Trim();
                string right = normalizedSep.Substring(sep + 2).Trim();
                if (right.Length == 0) return key;
                if (left.Length == 0 || ExpandAliasToken(left).Any(l => aliases.Any(a => String.Equals(a, l, StringComparison.OrdinalIgnoreCase))))
                    return prefix + right;
                return key;
            }
            if (aliases.Any(a => String.Equals(a, key, StringComparison.OrdinalIgnoreCase))) return key;
            foreach (string alias in aliases.OrderByDescending(a => a.Length))
            {
                if (key.StartsWith(alias, StringComparison.OrdinalIgnoreCase) && key.Length > alias.Length)
                {
                    string tail = key.Substring(alias.Length).TrimStart();
                    if (tail.Length > 1 && (tail[0] == '—' || tail[0] == '-' || tail[0] == '–' || tail[0] == '·' || tail[0] == ':' || tail[0] == '：'))
                    {
                        string outfit = tail.Substring(1).Trim();
                        if (outfit.Length > 0) return prefix + outfit;
                    }
                }
            }
            return prefix + key;
        }

        private static void NormalizeCharacter(VisualSettingEntry entry)
        {
            if (entry == null || entry.kind != "人物" || String.IsNullOrWhiteSpace(entry.identity)) return;
            string identity = entry.identity.Trim();
            string key = (entry.outfitKey ?? "").Trim();
            string parent = (entry.parentOutfitKey ?? "").Trim();
            string state = (entry.stateName ?? "").Trim();
            string prefix = identity + "::";
            if (key.Contains("::") || key.Contains("：："))
            {
                string canonical = NormalizeOutfitKey(identity, key);
                if (canonical.StartsWith(prefix, StringComparison.Ordinal) && canonical.Length > prefix.Length)
                {
                    entry.kind = "造型";
                    entry.outfitKey = canonical;
                    if (!String.IsNullOrWhiteSpace(parent)) entry.parentOutfitKey = NormalizeOutfitKey(identity, parent);
                    return;
                }
            }
            if (entry.isOutfitBase) return;
            if ((key.Length == 0 || IdentityAliases(identity).Any(a => String.Equals(a, key, StringComparison.OrdinalIgnoreCase))) && parent.Length == 0 &&
                new[] {"", "无", "常态", "基准态", "基础", "本体", "默认"}.Contains(state))
            {
                entry.isOutfitBase = true;
                return;
            }
            throw new InvalidDataException("视觉条目「" + entry.name + "」（" + identity +
                "）分类不明确。人物本体请填 kind=人物、isOutfitBase=true；独立服装状态请填 kind=造型、" +
                "outfitKey=" + prefix + "套装名、parentOutfitKey=outfitKey 和具体 stateName，并提供对应基准态。");
        }

        internal static IEnumerable<AnalysisAsset> Parents(AnalysisAsset child, IEnumerable<AnalysisAsset> assets)
        {
            return assets.Where(a => a.isBaseState && String.Equals(a.category, child.category, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.identity, child.identity, StringComparison.OrdinalIgnoreCase) &&
                ((!String.IsNullOrWhiteSpace(a.outfitKey) && String.Equals(a.outfitKey, child.outfitKey, StringComparison.OrdinalIgnoreCase)) ||
                 String.Equals(a.category + "|" + a.identity + "|" + a.variant, child.parentAssetKey, StringComparison.OrdinalIgnoreCase)));
        }

        private static string InlineValues(IEnumerable<string> values)
        {
            return String.Join("；", values.Where(v => v != null).SelectMany(v => v.Split(new[] { '\r', '\n', '；' }))
                .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string Joined(IEnumerable<string> values)
        {
            return String.Join("\n",values.Where(v=>!String.IsNullOrWhiteSpace(v)).Distinct());
        }

        internal static VisualSettingDoc CombineVisual(List<VisualSettingDoc> docs)
        {
            var result=new VisualSettingDoc {ProductionFormat=InlineValues(docs.Select(d=>d.ProductionFormat)),
                EraBackground=InlineValues(docs.Select(d=>d.EraBackground)),Entries=new List<VisualSettingEntry>(),
                PendingDecisions=docs.SelectMany(d=>d.PendingDecisions).Distinct().ToList()};
            foreach (var group in docs.SelectMany(d=>d.Entries).GroupBy(e=>new { e.kind, identity=e.identity.Trim().ToUpperInvariant(),
                outfit=(e.outfitKey??"").Trim().ToUpperInvariant(), state=e.isOutfitBase ? "" : (e.stateName??"").Trim().ToUpperInvariant(), e.isOutfitBase }))
            {
                var e=group.First();
                result.Entries.Add(new VisualSettingEntry {kind=e.kind,name=e.name,identity=e.identity.Trim(),outfitKey=(e.outfitKey??"").Trim(),
                    stateName=e.stateName,isOutfitBase=e.isOutfitBase,parentOutfitKey=e.parentOutfitKey,
                    appearance=Joined(group.Select(a=>a.appearance)),episodeState=Joined(group.Select(a=>a.episodeState)),
                    voiceDirection=Joined(group.Select(a=>a.voiceDirection)),promptName=Joined(group.Select(a=>a.promptName)),
                    propTextPolicy=Joined(group.Select(a=>a.propTextPolicy)),continuityLocks=group.SelectMany(a=>a.continuityLocks).Distinct().ToList(),
                    evidence=group.SelectMany(a=>a.evidence).Distinct().OrderBy(n=>n).ToList()});
            }
            result.Markdown=VisualMarkdown(result);
            return result;
        }

        internal static string VariantName(VisualSettingEntry e)
        {
            string prefix=(e.identity??"").Trim()+"::";
            string key=(e.outfitKey??"").Trim();
            string outfit=e.kind=="造型" && key.StartsWith(prefix,StringComparison.Ordinal) ? key.Substring(prefix.Length) : "";
            return String.Join(" · ",new[]{outfit,e.isOutfitBase ? "" : (e.stateName??"").Trim()}.Where(v=>v.Length>0));
        }

        internal static AnalysisAsset Map(VisualSettingEntry e,string era)
        {
            if (e == null) throw new InvalidDataException("缺少视觉条目。");
            var missing = new List<string>();
            if (!new[]{"人物","造型","地点","道具"}.Contains(e.kind)) missing.Add("类别 kind");
            if (String.IsNullOrWhiteSpace(e.name)) missing.Add("名称 name");
            if (String.IsNullOrWhiteSpace(e.identity)) missing.Add("本体 identity");
            if (String.IsNullOrWhiteSpace(e.appearance)) missing.Add("外观 appearance");
            if (e.evidence == null || e.evidence.Count == 0) missing.Add("原文依据 evidence");
            if (e.continuityLocks == null) e.continuityLocks = new List<string>();
            if (missing.Count != 0) throw new InvalidDataException("条目「" + (e.name ?? e.identity) + "」缺少有效字段：" + String.Join("、", missing));
            string identity=e.identity.Trim();
            string category=e.kind=="地点" ? "场景" : e.kind=="造型" ? "人物" : e.kind;
            string key=(e.outfitKey??"").Trim();
            if (e.kind == "造型" && key.Length > 0)
            {
                key = NormalizeOutfitKey(identity, key);
                e.outfitKey = key;
                if (!String.IsNullOrWhiteSpace(e.parentOutfitKey))
                    e.parentOutfitKey = NormalizeOutfitKey(identity, e.parentOutfitKey);
            }
            if (!e.isOutfitBase && key.Length == 0 && (e.kind == "道具" || e.kind == "地点"))
            {
                key = String.IsNullOrWhiteSpace(e.parentOutfitKey) ? identity : e.parentOutfitKey.Trim();
                e.outfitKey = e.parentOutfitKey = key;
            }
            string prefix=identity+"::";
            if (e.kind=="造型" && (!key.StartsWith(prefix,StringComparison.Ordinal) || key.Length<=prefix.Length))
                throw new InvalidDataException("造型「"+e.name+"」的 outfitKey 应为「"+identity+"::套装名」。");
            if (e.kind=="人物" && !e.isOutfitBase) throw new InvalidDataException("有独立状态图的造型应归入造型条目。");
            if (!e.isOutfitBase && (key.Length==0 || (e.parentOutfitKey??"").Trim()!=key || String.IsNullOrWhiteSpace(e.stateName)))
                throw new InvalidDataException("条目「"+e.name+"」（"+identity+"，"+key+"）：状态变体必须指向同套装、同空间或同道具的基准态并填状态名。");
            string basis=e.kind=="造型" ? key.Substring(prefix.Length) : "";
            string variant=VariantName(e);
            string direction=String.Join("\n", Fields(e,era));
            return new AnalysisAsset { category=category,identity=identity,variant=variant,name=variant.Length==0 ? identity : identity+" · "+variant,
                outfitKey=key,isBaseState=e.isOutfitBase,parentAssetKey=e.isOutfitBase ? "" : category+"|"+identity+"|"+basis,
                evidence=e.evidence.ToList(),appearance=e.appearance,episodeState=e.episodeState,voiceDirection=e.voiceDirection,
                promptName=e.promptName,eraBackground=era,propTextPolicy=e.propTextPolicy,continuityLocks=e.continuityLocks.ToList(),
                direction=direction,reason="视觉设定 · "+e.name };
        }

        private static IEnumerable<string> Fields(VisualSettingEntry e,string era)
        {
            yield return "外观特征："+e.appearance;
            yield return "本集状态："+e.episodeState;
            if (!String.IsNullOrWhiteSpace(era)) yield return "时代背景："+era;
            if (!String.IsNullOrWhiteSpace(e.voiceDirection)) yield return "角色声线："+e.voiceDirection;
            if (!String.IsNullOrWhiteSpace(e.promptName)) yield return "提示词名称："+e.promptName;
            if (!String.IsNullOrWhiteSpace(e.propTextPolicy)) yield return "道具字迹："+e.propTextPolicy;
            foreach (var item in e.continuityLocks) yield return "连戏锁："+item;
        }

        internal static string VisualMarkdown(VisualSettingDoc doc)
        {
            var sb=new StringBuilder("# 视觉设定\n\n");
            sb.AppendLine("- 制作形态："+doc.ProductionFormat).AppendLine("- 时代背景："+doc.EraBackground);
            foreach (var e in doc.Entries)
            {
                sb.AppendLine().AppendLine("## "+e.kind+" · "+e.name).AppendLine();
                foreach (var line in Fields(e,null)) sb.AppendLine("- "+line);
                sb.AppendLine("- 原文依据："+String.Join("、",e.evidence.Select(n=>"L"+n)));
            }
            return sb.ToString();
        }

        internal static string DirectorMarkdown(DirectorAnalysisDoc doc)
        {
            var sb=new StringBuilder("# "+doc.Title+"\n\n");
            sb.AppendLine("| 集 | 场次 | 场景 | 剧情核心（分集节拍／本场） | 视觉重点 | 必需镜头 | 情绪重点 | 服装 | 场景氛围要求 | Key Prop | 特殊要求说明 | 导演问题/说明 |");
            sb.AppendLine("|"+String.Join("|",Enumerable.Repeat("---",12))+"|");
            foreach (var d in doc.Scenes)
            {
                var cells=new[]{d.episode,d.sceneCode,d.sceneHeading,String.Join("\n",new[]{d.beats,d.plotCore}.Where(t=>!String.IsNullOrEmpty(t))),
                    d.visualFocus,String.Join("\n",d.requiredShots),d.emotionFocus,d.costume,d.atmosphere,d.keyProps,d.specialNotes,""};
                sb.AppendLine("| "+String.Join(" | ",cells.Select(t=>(t??"").Replace("|","\\|").Replace("\r","").Replace("\n","<br>")))+" |");
            }
            if (doc.Doubts!=null && doc.Doubts.Count>0)
                sb.AppendLine().AppendLine("原稿疑点").AppendLine(String.Join("\n",doc.Doubts.Select(d=>"- "+d)));
            return sb.ToString();
        }

        internal static string DirectorJsonPath(string root, string versionId)
        {
            if (!AgentData.ValidId(versionId)) throw new InvalidDataException("剧本分析版本无效。");
            return AgentData.Inside(root, Path.Combine("剧本分析", versionId, "导演分析.json"));
        }

        internal static string DirectorMdPath(string root, string versionId)
        {
            if (!AgentData.ValidId(versionId)) throw new InvalidDataException("剧本分析版本无效。");
            return AgentData.Inside(root, Path.Combine("剧本分析", versionId, "导演剧本分析.md"));
        }

        internal static string VisualJsonPath(string root, string versionId)
        {
            if (!AgentData.ValidId(versionId)) throw new InvalidDataException("剧本分析版本无效。");
            return AgentData.Inside(root, Path.Combine("剧本分析", versionId, "视觉设定.json"));
        }

        internal static string VisualMdPath(string root, string versionId)
        {
            if (!AgentData.ValidId(versionId)) throw new InvalidDataException("剧本分析版本无效。");
            return AgentData.Inside(root, Path.Combine("剧本分析", versionId, "视觉设定.md"));
        }

        internal static DirectorAnalysisDoc LoadDirectorNotes(string root, string versionId)
        {
            if (String.IsNullOrEmpty(root) || !AgentData.ValidId(versionId)) return null;
            try { return AgentData.Read<DirectorAnalysisDoc>(DirectorJsonPath(root, versionId)); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        internal static VisualSettingDoc LoadVisualSettings(string root, string versionId)
        {
            if (String.IsNullOrEmpty(root) || !AgentData.ValidId(versionId)) return null;
            try { return AgentData.Read<VisualSettingDoc>(VisualJsonPath(root, versionId)); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        internal static DirectorSceneNote MatchScene(DirectorAnalysisDoc doc, int first, int last, string title)
        {
            if (doc == null || doc.Scenes == null) return null;
            var overlap = doc.Scenes.FirstOrDefault(s => s != null && s.first <= last && s.last >= first);
            if (overlap != null) return overlap;
            return doc.Scenes.FirstOrDefault(s => s != null && !String.IsNullOrWhiteSpace(title) &&
                !String.IsNullOrWhiteSpace(s.sceneHeading) && title.IndexOf(s.sceneHeading, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static List<string> ContinuityLocksFor(VisualSettingDoc doc, IEnumerable<string> assetNames)
        {
            var result = new List<string>();
            if (doc == null || doc.Entries == null) return result;
            var targets = new HashSet<string>((assetNames ?? Enumerable.Empty<string>()).Where(n => !String.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.Entries)
            {
                if (entry == null || entry.continuityLocks == null || entry.continuityLocks.Count == 0) continue;
                string mapped=String.Join(" · ",new[]{entry.identity,VariantName(entry)}.Where(v=>!String.IsNullOrWhiteSpace(v)));
                bool matched = targets.Contains(entry.name) || targets.Contains(mapped) ||
                    (entry.kind=="造型" && entry.isOutfitBase && targets.Any(t=>t.StartsWith(mapped+" · ",StringComparison.OrdinalIgnoreCase))) ||
                    (entry.kind=="人物" && targets.Any(t=>t==entry.identity || t.StartsWith(entry.identity+" · ",StringComparison.OrdinalIgnoreCase)));
                if (!matched) continue;
                foreach (var item in entry.continuityLocks)
                    if (!String.IsNullOrWhiteSpace(item) && !result.Contains(item)) result.Add(item);
            }
            return result;
        }

        internal static Dictionary<string, string> VoicesFor(VisualSettingDoc doc)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || doc.Entries == null) return result;
            foreach (var entry in doc.Entries)
            {
                if (entry == null || String.IsNullOrWhiteSpace(entry.voiceDirection)) continue;
                string key = !String.IsNullOrWhiteSpace(entry.identity) ? entry.identity : entry.name;
                if (!String.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                    result[key] = entry.voiceDirection;
            }
            return result;
        }

        internal static void SavePublishedArtifacts(string root, string versionId, DirectorAnalysisDoc director, VisualSettingDoc visual)
        {
            if (director != null)
            {
                director.VersionId = versionId;
                AgentData.Save(DirectorJsonPath(root, versionId), director);
                File.WriteAllText(DirectorMdPath(root,versionId), DirectorMarkdown(director), new UTF8Encoding(true));
            }
            if (visual != null)
            {
                visual.VersionId = versionId;
                AgentData.Save(VisualJsonPath(root, versionId), visual);
                if (!String.IsNullOrWhiteSpace(visual.Markdown))
                    File.WriteAllText(VisualMdPath(root, versionId), visual.Markdown, new System.Text.UTF8Encoding(true));
            }
        }
    }
}
