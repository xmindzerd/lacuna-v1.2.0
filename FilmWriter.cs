using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    internal static class FilmWriter
    {
        public sealed class BoardReply
        {
            public List<FilmShot> shots { get; set; }
        }

        public sealed class PromptPiece
        {
            public int number { get; set; }
            public string direction { get; set; }
        }

        public sealed class PromptReply
        {
            public List<PromptPiece> pieces { get; set; }
        }

        private const string BoardSchema = @"{'type':'object','properties':{'shots':{'type':'array','items':{'type':'object','properties':{'first':{'type':'integer'},'last':{'type':'integer'},'seconds':{'type':'integer'},'action':{'type':'string'},'camera':{'type':'string'},'end':{'type':'string'},'angle':{'type':'string','enum':['主图','侧面','斜侧','广景','反打']},'assets':{'type':'array','items':{'type':'string'}}},'required':['first','last','seconds','action','camera','end','angle','assets'],'additionalProperties':false}}},'required':['shots'],'additionalProperties':false}";
        private const string V2BoardSchema = @"{'type':'object','properties':{'shots':{'type':'array','items':{'type':'object','properties':{'code':{'type':'string'},'content':{'type':'string'},'continuation':{'type':'boolean'},'dialogueSlice':{'type':'string'},'mustShotRefs':{'type':'array','items':{'type':'string'}},'first':{'type':'integer'},'last':{'type':'integer'},'seconds':{'type':'integer'},'action':{'type':'string'},'camera':{'type':'string'},'end':{'type':'string'},'angle':{'type':'string','enum':['主图','侧面','斜侧','广景','反打']},'assets':{'type':'array','items':{'type':'string'}}},'required':['code','content','continuation','dialogueSlice','mustShotRefs','first','last','seconds','action','camera','end','angle','assets'],'additionalProperties':false}}},'required':['shots'],'additionalProperties':false}";
        private const string TextSchema = @"{'type':'object','properties':{'pieces':{'type':'array','items':{'type':'object','properties':{'number':{'type':'integer'},'direction':{'type':'string'}},'required':['number','direction'],'additionalProperties':false}}},'required':['pieces'],'additionalProperties':false}";
        internal static T Ask<T>(string exe, string model, SelectedSkill skill, string schema, string instruction, CancellationToken token, Action<string> progress, bool audit = false, bool retryInvalidOutput = true)
            where T : class
        {
            return AskStructured<T>(exe,model,skill,schema,instruction,token,progress,audit,retryInvalidOutput,null);
        }

        private static T AskStructured<T>(string exe, string model, SelectedSkill skill, string schema, string instruction,
            CancellationToken token, Action<string> progress, bool audit, bool retryInvalidOutput, string effort) where T : class
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "一键成片-FILM-" + Film.Id())).FullName;
            try
            {
                var shape = Path.Combine(folder, "shape.json");
                var result = Path.Combine(folder, "result.json");
                File.WriteAllText(shape, schema.Replace('\'', '"'), new UTF8Encoding(false));
                for (int attempt = 0;; attempt++)
                {
                    try
                    {
                        if (File.Exists(result))
                            File.Delete(result);
                        var streamClock=System.Diagnostics.Stopwatch.StartNew();
                        var response = AgentRunner.Run(exe, folder, instruction, model, null, shape, result, null, null, null, false, e =>
                        {
                            if(e.Type=="delta" && progress!=null && streamClock.ElapsedMilliseconds>=3000)
                            {progress("Codex 正在生成结构化正文…");streamClock.Restart();}
                            if (progress != null && (e.Type == "session" || e.Type == "complete" || e.Type == "step" || e.Type == "result_wait"))
                                progress(e.Type == "session" ? "本机 Codex 已接收 · " + e.Text : e.Type == "complete" ? "Codex 已结束，等待读取结果" : e.Type == "result_wait" ? e.Text : "Codex 正在读取资料");
                        }, token, skill: skill, skillAudit: audit, effortOverride: effort, timeoutMs: 900000, idleTimeoutMs: 360000);
                        token.ThrowIfCancellationRequested();
                        if (progress != null) progress("已读取结果，正在核对内容");
                        try { return AgentData.ParseOutput<T>(response.StructuredText); }
                        catch (InvalidDataException e)
                        {
                            e.Data["failedOutput"] = AgentData.Short(FlowAutoRepair.Safe(response.StructuredText),24000);
                            throw;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        if (attempt != 0 || !retryInvalidOutput)
                            throw;
                        token.ThrowIfCancellationRequested();
                        if (progress != null)
                            progress("本轮已结束但没有有效结果，正在单独重试本次文字请求。");
                    }
                }
            }
            catch (TimeoutException e)
            {
                throw new TimeoutException("本机 Codex 未在时限内交回本次文字工作，请在本场续做。已保存的其他场次可继续使用。", e);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private sealed class TranslationBatch
        {
            public List<TranslationLine> lines { get; set; }
        }

        private sealed class TranslationLine
        {
            public int index { get; set; }
            public string english { get; set; }
        }

        private static Dictionary<FilmCue,FlowRepairOutcome> TranslateDialogue(string exe, string model, List<FilmCue> cues,
            CancellationToken token, Action<string> progress)
        {
            const string schema = "{'type':'object','additionalProperties':false,'required':['lines'],'properties':{'lines':{'type':'array','items':{'type':'object','additionalProperties':false,'required':['index','english'],'properties':{'index':{'type':'integer'},'english':{'type':'string'}}}}}}";
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string prompt = "逐条将对白译为自然英文，保持人物与原意。每个 index 返回一次，english 仅含对应的一句对白。" +
                AgentData.Json().Serialize(cues.Select((c,i) => new { index=i+1, c.Speaker, c.Text }));
            var reply = AskStructured<TranslationBatch>(exe,model,null,schema,prompt,token,progress,false,false,"low");
            if (reply.lines == null || reply.lines.Count != cues.Count || reply.lines.Any(l => l == null) ||
                !reply.lines.Select(l => l.index).OrderBy(n=>n).SequenceEqual(Enumerable.Range(1,cues.Count)))
                throw new InvalidDataException("英文对白补配失败，译文索引重复或遗漏。");
            var translations = new Dictionary<FilmCue,FlowRepairOutcome>();
            foreach (var line in reply.lines)
            {
                var cue = cues[line.index-1];
                string english = (line.english ?? "").Trim();
                if (!Regex.IsMatch(english,"[A-Za-z]") || Regex.IsMatch(english,@"[\u3400-\u9fff\r\n]") || FlowAutoRepair.ContainsCredentialLeak(english))
                    throw new InvalidDataException("第 " + cue.First + " 行英文对白补配失败。");
                translations.Add(cue,new FlowRepairOutcome { Succeeded=true, RepairedText=english, RootCause="原文缺少英文对白。",
                    OneShotFix="按原句索引批量补译后逐句核对。", PermanentCodeFix="在分镜前核对双语对白。",
                    ElapsedMs=translations.Count == 0 ? timer.ElapsedMilliseconds : 0 });
            }
            return translations;
        }

        internal static string Wardrobe(string exe, string model, SelectedSkill skill, FilmAsset asset, IEnumerable<string> source, CancellationToken token, Action<string> progress)
        {
            var cmd = "为这一个身份和服装版本写中文简短连续性说明。只采用已给剧本和现有条目的信息，缺少时写待确定。返回编号1。\n" + AgentData.Json().Serialize(new { asset.Name, asset.Identity, asset.Look, source = source.Take(60) });
            var answer = Ask<PromptReply>(exe, model, skill, TextSchema, cmd, token, progress);
            if (answer.pieces == null || answer.pieces.Count != 1 || String.IsNullOrWhiteSpace(answer.pieces[0].direction))
                throw new InvalidDataException("未取得造型说明。");
            return answer.pieces[0].direction;
        }

        private static bool VerticalSkill(SelectedSkill skill)
        {
            return skill != null && ((skill.Reference.Name + " " + skill.Reference.Id + " " + skill.Directory)
                .IndexOf(BuiltinSkills.VerticalShotlistId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                skill.Parts != null && skill.Parts.Any(VerticalSkill));
        }

        internal static bool IsOneClickPromptSkill(SelectedSkill skill)
        {
            if (skill == null) return false;
            var reference = skill.Reference;
            string identity = (reference == null ? "" : reference.Id + " " + reference.Name) + " " + skill.Directory;
            return CanvasIdentity(identity) ||
                (skill.Parts != null && skill.Parts.Any(IsOneClickPromptSkill));
        }

        private static bool CanvasIdentity(string identity)
        {
            return identity != null && identity.IndexOf(BuiltinSkills.DefaultFinalPromptId,StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private const string CanvasTail = "全局禁止出现任何字幕、音乐、非同期声音效、转场。保留真实的微细毛孔、肌肤纹理与皮肤质感。人物表演克制准确。";

        private static object SceneGlobalFacts(FilmBook b, FilmScene s, SkillReference skill)
        {
            var assets = s.Shots.SelectMany(sh => Assets(b,sh)).Distinct().ToList();
            var names = new HashSet<string>(assets.SelectMany(a => new[] { a.Name, a.Identity }), StringComparer.OrdinalIgnoreCase);
            return new { s.Id, s.SourceHash, b.AnalysisId, b.DeliveryId, b.Brief.Style, b.Brief.Camera, b.Brief.Ratio, b.Brief.Language,
                s.Title, s.DirectorSummary, director = DirectorPipeline.MatchScene(b.Director,s.First,s.Last,s.Title),
                productionFormat = b.Visual == null ? null : b.Visual.ProductionFormat,
                visualSettings = VisualFacts(b,assets,s.Cues.Where(c => Film.Spoken(c,b.Brief.Language)).Select(c => c.Speaker)),
                appearance = b.Visual == null || b.Visual.Entries == null ? null : b.Visual.Entries.Where(e => e != null &&
                    (names.Contains(e.name) || names.Contains(e.identity))).Select(e => new { e.name, e.identity, e.appearance }),
                assets = assets.Select(a => new { a.Id, a.Name, a.Category, a.Look }), shots = s.Shots, skillId = skill.Id, skill.Digest };
        }

        private static bool MatchesSceneAsset(FilmAsset asset, VisualSettingEntry entry)
        {
            if (String.Equals(entry.name,asset.Name,StringComparison.OrdinalIgnoreCase)) return true;
            if (!String.Equals(entry.identity,asset.Identity,StringComparison.OrdinalIgnoreCase)) return false;
            string variant = DirectorPipeline.VariantName(entry);
            string displayName = String.IsNullOrEmpty(variant) ? entry.identity : entry.identity + " · " + variant;
            return String.Equals(displayName,asset.Name,StringComparison.OrdinalIgnoreCase);
        }

        private static object SceneGlobalPromptPayload(FilmBook b, FilmScene s)
        {
            var speakers = new HashSet<string>(s.Cues.Where(c => Film.Spoken(c,b.Brief.Language)).Select(c => c.Speaker),StringComparer.OrdinalIgnoreCase);
            var entries = b.Visual == null || b.Visual.Entries == null ? new List<VisualSettingEntry>() : b.Visual.Entries;
            var assets = s.Shots.SelectMany(sh => Assets(b,sh)).Distinct().Select(a => {
                var matches = entries.Where(e => e != null && MatchesSceneAsset(a,e)).ToList();
                return new { a.Name, a.Category, a.Identity, appearance = String.Join("；",new[]{a.Look}.Concat(matches.Select(e => e.appearance))
                        .Where(t => !String.IsNullOrWhiteSpace(t)).Distinct()),
                    continuityLocks = matches.SelectMany(e => e.continuityLocks ?? new List<string>()).Distinct() };
            });
            return new { s.Title, b.Brief.Style, b.Brief.Camera, b.Brief.Ratio, b.Brief.Language, s.DirectorSummary,
                director = DirectorPipeline.MatchScene(b.Director,s.First,s.Last,s.Title), assets,
                productionFormat = b.Visual == null ? null : b.Visual.ProductionFormat,
                eraBackground = b.Visual == null ? null : b.Visual.EraBackground,
                voices = DirectorPipeline.VoicesFor(b.Visual).Where(v => speakers.Contains(v.Key)).ToDictionary(v=>v.Key,v=>v.Value),
                shots = s.Shots.Select(sh => new { sh.code, sh.camera, sh.action }) };
        }

        internal static bool EnsureSceneGlobalPrompt(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s,
            CancellationToken token, Action<string> progress)
        {
            return EnsureSceneGlobalPromptForFlow(exe,model,skill,b,s,token,progress,null);
        }

        internal static bool EnsureSceneGlobalPromptForFlow(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s,
            CancellationToken token, Action<string> progress, string projectRoot)
        {
            if (!IsOneClickPromptSkill(skill)) return false;
            lock (s)
            {
                var facts = SceneGlobalFacts(b,s,skill.Reference);
                string sig = AgentData.Hash(AssetProduction.StableJson(facts));
                if (!String.IsNullOrWhiteSpace(s.GlobalPrompt) && s.GlobalPromptSignature == sig) return false;
                skill = BuiltinSkills.WithPreloadedReferences(skill,"references/stable-conditions.md","references/scene-canvas.md");
                string cmd = "Stage 2 场级全局正文。" + (skill != null && skill.PreloadedReferences ? "按已预载的 stable-conditions.md 和 scene-canvas.md 规则（无需调用工具）" : "按 references/stable-conditions.md 和 references/scene-canvas.md ") + "形成并默认通过本场固定条件。" +
                    "只返回编号 1 的 direction 纯文本。首行视觉风格：，另行全局光影参考：，按需逐行给室外场地环境参考、群演外观参考和动作参考。" +
                    "全部说话人（含 VO/OS）的已定声线/音色在上述参考行之后逐人单列，保留 voices 的原话。不要写全局场景/人物/道具参考，单段组装时精确列出。" +
                    "不要询问确认，不要运行 export-canvas.mjs 或 Python 脚本。末段逐字写：" + CanvasTail + "\n制作事实：" + AgentData.Json().Serialize(SceneGlobalPromptPayload(b,s));
                var repairTime = new System.Diagnostics.Stopwatch();
                FlowRepairOutcome fix = null;
                string failed = "", error = "";
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        var reply = fix == null ? Ask<PromptReply>(exe,model,skill,TextSchema,cmd,token,progress,retryInvalidOutput:false) :
                            new PromptReply { pieces=new List<PromptPiece>{new PromptPiece { number=1, direction=fix.RepairedText }} };
                        var piece = reply.pieces != null && reply.pieces.Count == 1 ? reply.pieces[0] : null;
                        string body = piece == null ? "" : (piece.direction ?? "").Replace("\r\n","\n").Replace('\r','\n').Trim();
                        failed = body;
                        var speakers = s.Cues.Where(c => Film.Spoken(c,b.Brief.Language)).Select(c => c.Speaker);
                        var lines = body.Split('\n').Select(l => Regex.Replace(l.Trim(),@"^(视觉风格|全局光影参考)\s*[:：]\s*","$1："))
                            .Where(l => l.Length > 0 && !Regex.IsMatch(l,@"^全局(?:场景|人物|角色|道具)参考\s*[：:]")).ToList();
                        if (lines.Count == 0 || !Regex.IsMatch(lines[0],@"^视觉风格：\S") || !lines.Any(l => Regex.IsMatch(l,@"^全局光影参考：\S")) ||
                            piece == null || piece.number != 1)
                            throw new InvalidDataException("场级正文需要首行视觉风格与独立全局光影参考。");
                        if (lines.Last() == CanvasTail || OrderedOverlap(lines.Last(),CanvasTail) >= 0.65) lines.RemoveAt(lines.Count-1);
                        foreach (var voice in DirectorPipeline.VoicesFor(b.Visual).Where(v => speakers.Contains(v.Key,StringComparer.OrdinalIgnoreCase)))
                            if (!lines.Any(l => l.Contains(voice.Key) && l.Contains(voice.Value))) lines.Add(voice.Key + "声线：" + voice.Value);
                        lines.Add(CanvasTail);
                        body = String.Join("\n",lines);
                        RecordCorrection(projectRoot,model,4,s.Title,"FilmWriter.EnsureSceneGlobalPrompt",
                            FlowIncidentCategory.FinalPromptValidation,error,fix,repairTime);
                        lock (b) { s.GlobalPrompt = body; s.GlobalPromptSignature = sig; }
                        return true;
                    }
                    catch (InvalidDataException ex)
                    {
                        repairTime.Start();
                        error=ex.Message;
                        if (attempt > 1 || (attempt == 1 && String.IsNullOrEmpty(projectRoot))) throw;
                        if (attempt == 1)
                        {
                            fix = FlowAutoRepair.ResolveTextIssue(projectRoot,exe,model,skill,FlowHalf.PostAssets,4,"场级全局",s.Title,
                                "FilmWriter.EnsureSceneGlobalPrompt",cmd,error + "\n" + (ex.Data["failedOutput"] as string ?? failed),token,progress);
                            if (!fix.Succeeded) throw new InvalidDataException(error + "；" + fix.RootCause);
                        }
                        cmd += "\n请纠正：" + ex.Message;
                    }
                }
            }
        }

        private static object VisualFacts(FilmBook book, IEnumerable<FilmAsset> assets, IEnumerable<string> speakers = null)
        {
            var names = new HashSet<string>(assets.SelectMany(a => new[] { a.Name, a.Identity }).Where(n => !String.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            var voiceKeys = new HashSet<string>(speakers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var entries = book.Visual == null || book.Visual.Entries == null ? new List<VisualSettingEntry>() :
                book.Visual.Entries.Where(e => e != null && (names.Contains(e.name) || names.Contains(e.identity))).ToList();
            return new { eraBackground = book.Visual == null ? null : book.Visual.EraBackground,
                voices = DirectorPipeline.VoicesFor(book.Visual).Where(v => voiceKeys.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value),
                assets = entries.Select(e => new { e.name, e.identity, e.outfitKey, e.stateName, e.continuityLocks, e.voiceDirection }) };
        }

        internal static void CheckRequiredShots(List<FilmShot> all, IEnumerable<string> requiredShots)
        {
            if (all.Count(sh => sh.seconds > 3) > Math.Max(1, all.Count / 20))
                throw new InvalidDataException("本场超过 3 秒的情绪镜过多，最多允许 " + Math.Max(1, all.Count / 20) + " 镜。超时镜号：" + String.Join("、",all.Select((sh,i) => sh.seconds > 3 ? sh.code ?? (i+1).ToString() : null).Where(x => x != null)));
            var requirements = requiredShots.Distinct().ToDictionary(item => item, NormalizeShotRef);
            var covered = new HashSet<string>();
            foreach (var shot in all)
            {
                if (shot.mustShotRefs == null || shot.mustShotRefs.Count == 0) continue;
                string body = Regex.Replace(shot.content ?? "", @"^\s*[\[【（(]?\s*(?:时长\s*[：:]?\s*)?\d+(?:\.\d+)?\s*(?:秒|s)", "");
                if (Regex.Replace(body, @"[^\p{L}\p{N}]", "").Length < 10) continue;
                foreach (var reference in shot.mustShotRefs)
                {
                    string cited = NormalizeShotRef(reference);
                    if (cited.Length == 0) continue;
                    var matches = requirements.Where(kv => kv.Value == cited).ToList();
                    if (matches.Count == 0)
                        matches = requirements.Where(kv => kv.Value.StartsWith(cited, StringComparison.Ordinal) ||
                            kv.Value.Length > 0 && cited.StartsWith(kv.Value, StringComparison.Ordinal)).ToList();
                    if (matches.Count == 0)
                        matches = requirements.Where(kv => Math.Min(cited.Length,kv.Value.Length) >= 3 &&
                            (kv.Value.Contains(cited) || cited.Contains(kv.Value) || OrderedOverlap(cited,kv.Value) >= 0.65)).ToList();
                    if (matches.Count == 1) covered.Add(matches[0].Key);
                }
            }
            var missing = requirements.Keys.Except(covered).ToList();
            if (missing.Count > 0) throw new InvalidDataException("缺少导演必需镜头：" + String.Join("、", missing));
        }

        private static double OrderedOverlap(string a, string b)
        {
            // Ordered character overlap (LCS), normalized by the longer reference.
            if (a.Length > 512 || b.Length > 512) return 0;
            var row = new int[b.Length+1];
            foreach (char c in a)
            {
                int previous = 0;
                for (int j=1; j<=b.Length; j++)
                {
                    int old = row[j];
                    row[j] = c == b[j-1] ? previous+1 : Math.Max(old,row[j-1]);
                    previous = old;
                }
            }
            return (double)row[b.Length] / Math.Max(a.Length,b.Length);
        }

        private static string NormalizeCode(string code)
        {
            string value = Regex.Replace((code ?? "").Trim().ToUpperInvariant(),@"\s+","").Replace('－','-').Replace('–','-');
            return Regex.Replace(value,@"^0*(\d+)-0*(\d+)([A-Z]*)$","$1-$2$3");
        }

        private static void RecordCorrection(string root,string model,int stage,string step,string hint,string category,
            string error,FlowRepairOutcome fix,System.Diagnostics.Stopwatch timer)
        {
            if (String.IsNullOrEmpty(root) || !timer.IsRunning) return;
            if (fix == null) fix = new FlowRepairOutcome { Succeeded=true, RootCause="原稿未通过 " + hint + " 的校验。",
                OneShotFix="反馈原始校验结果后重新生成，复核通过。", PermanentCodeFix="按该校验点补充提示约束与同类输入回归。" };
            fix.ElapsedMs=timer.ElapsedMilliseconds;
            FlowAutoRepair.RecordPost(root,model,stage,step,hint,category,error,fix);
        }

        private static string NormalizeShotRef(string text)
        {
            return Regex.Replace(text ?? "", @"[\s\p{P}]+", "");
        }

        private static string ShotSuffix(int index)
        {
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            string suffix = "";
            for (int n = index + 1; n > 0; n = (n - 1) / letters.Length)
                suffix = letters[(n - 1) % letters.Length] + suffix;
            return suffix;
        }

        internal static List<FilmShot> Board(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s, CancellationToken token, Action<string> progress,VideoModel target=null)
        {
            return BoardForFlow(exe,model,skill,b,s,token,progress,target,null);
        }

        internal static List<FilmShot> BoardForFlow(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s, CancellationToken token, Action<string> progress,VideoModel target, string projectRoot)
        {
            if (b.Brief.Language == "英文")
            {
                var missingLines = s.Cues.Where(c => c.Type == "对白" && c.Language == null && Regex.IsMatch(c.Text, @"[\u3400-\u9fff]")).ToList();
                if (missingLines.Count > 0)
                {
                    if (!b.V2Builtin || String.IsNullOrEmpty(projectRoot)) throw new InvalidDataException("原文第 " + missingLines[0].First + " 行缺少已核对的英文对白。请在分镜的原文校订中补录或核实英文原句。");
                    var translations = TranslateDialogue(exe,model,missingLines,token,progress);
                    lock (b)
                    {
                        token.ThrowIfCancellationRequested();
                        var originalCues = s.Cues;
                        s.Cues = new List<FilmCue>(originalCues);
                        try
                        {
                            foreach (var missing in missingLines)
                            {
                                var translated = new FilmCue { Id=Film.Id(), First=missing.Last, Last=missing.Last,
                                    Type=missing.Type, Speaker=missing.Speaker, Voice=missing.Voice, Note=missing.Note,
                                    Text=translations[missing].RepairedText.Trim(), Language="英文" };
                                missing.Language = "中文";
                                s.Cues.Insert(s.Cues.IndexOf(missing)+1, translated);
                            }
                            Film.Save(projectRoot, b);
                        }
                        catch
                        {
                            s.Cues = originalCues;
                            foreach (var missing in missingLines) missing.Language = null;
                            throw;
                        }
                    }
                    foreach (var missing in missingLines)
                        FlowAutoRepair.RecordPost(projectRoot,model,3,s.Title + " 原文行 " + missing.First,"FilmWriter.Board",
                            FlowIncidentCategory.StoryboardValidation,"缺少英文对白",translations[missing]);
                }
            }
            bool vertical = b.V2Builtin || VerticalSkill(skill);
            if (VerticalSkill(skill))
                skill = BuiltinSkills.WithPreloadedReferences(skill,b.Brief.Language == "中文" ?
                    new[]{"references/director-calibration.md"} : new[]{"references/director-calibration.md","references/north-america-vertical-short-drama.md"});
            var director = DirectorPipeline.MatchScene(b.Director, s.First, s.Last, s.Title);
            var sceneAssets = b.Assets.Where(a => a.Evidence == null || a.Evidence.Any(n => n >= s.First && n <= s.Last)).ToList();
            var visualSettings = VisualFacts(b, sceneAssets);
            string sceneCode = director == null ? null : director.sceneCode;
            string episode = director == null ? ProductionRules.Episode(s.Title ?? "") : director.episode;
            int ordinal = b.Scenes.TakeWhile(other => other != s).Count(other => !other.ProductionExcluded &&
                ProductionRules.Episode(other.Title ?? "") == ProductionRules.Episode(s.Title ?? "")) + 1;
            var requiredShots = s.MustShots ?? new List<string>();
            var shots = new List<FilmShot>();
            for (int offset = 0; offset < s.Cues.Count;)
            {
                var batch = s.Cues.Skip(offset).Take(24).ToList();
                while (batch.Count > 1 && batch.Sum(c => c.Text.Length) > 12000)
                    batch.RemoveAt(batch.Count - 1);
                if (Film.PairBoundary(s.Cues, offset + batch.Count)) batch.Add(s.Cues[offset + batch.Count]);
                // Present one addressable unit for an utterance and its translation.
                // Map model indices back to the original cue indices before saving the board.
                var spans = new List<List<FilmCue>>();
                for (int j = 0; j < batch.Count; j++)
                {
                    if (j == 0 || !Film.PairBoundary(s.Cues, offset + j)) spans.Add(new List<FilmCue>());
                    spans.Last().Add(batch[j]);
                }
                var data = new
                {
                    scene = s.Title, sceneCode, episode, sceneOrdinal = ordinal, director, visualSettings, language = b.Brief.Language,
                    nextShotSuffix = ShotSuffix(shots.Count),
                    style = b.Brief.Style,
                    photography = b.Brief.Camera,
                    format = b.Brief.Ratio,
                    targetSeconds = b.Brief.Target,
                    idea = b.Brief.Idea, background = offset == 0 ? AgentData.Short(b.Background,800) : null,
                    priorEnd = shots.Count > 0 ? shots.Last().end : null,
                    priorShots = shots.Skip(Math.Max(0, shots.Count - 5)).Select(sh => new { sh.action, sh.end, sh.assets }),
                    cast = b.Assets.Select(a => sceneAssets.Contains(a) ? (object)new { a.Id, a.Name, a.Look, a.Actor, a.Category, a.Scope, a.Panels,
                        angles = a.Images.Select(im => im.Angle).Distinct(), inScene = true } : new { a.Id, a.Name, a.Category, inScene = false }),
                    cues = spans.Select((span,i) => new { n=i+1, sourceFirst=span.First().First, sourceLast=span.Last().Last,
                        performance=span.Select(c=>new { c.Voice,c.Note }), content=span.Where(c=>c.Type != "对白" ||
                        Film.Spoken(c,b.Brief.Language)).Select(c=>new { c.Type,c.Speaker,c.Voice,c.Text }) })
                };
                string request = Film.LanguageRule(b.Brief.Language) + "同一单元的对白只发生一次。遇到闪回，只沿用有本场依据的造型；多状态拼图要依据剧情指定使用哪一格，无法判定时写明待核对。为 "+(b.VideoModelName??"Seedance 2.5")+" 做中文可拍摄的文字分镜，JSON 是剧本数据。按原序覆盖输入每个单元一次。每镜 first/last 是本批给出的连续 n 值，可含相邻译文和动作。seconds 为1至"+(target==null ? 30 : target.Max)+"整数，留足说话时间。用摄影与可见表演描述动作和末态，保留画外音，译文只供对照；跳过字卡、字幕及无画外音的纯色转场，将其行号随相邻实拍内容覆盖，不安排其时长，不重复表演；勿复述对白或添加台词。不得把大纲改编成新事件。危险情节用演员反应、画外声或剪切表达，避免操作细节。资产用实际入画者的准确 Id，按其当前造型选取，分批时继承此前已发生的换装或状态变化，直到剧本给出改变的依据。未出场角色不可引用。不要把完整剧集中所有资产都放入每镜。以下是原文：\n" + AgentData.Json().Serialize(data);
                if (vertical)
                    request = Film.LanguageRule(b.Brief.Language) + "按晓羽竖屏导演分镜规则，9:16，按 scene 场次和 language 配音，直接返回 JSON，无需提问或导出 Excel。保持原剧情和对白。镜号 code 按集数-场次和 nextShotSuffix 起连续编号，跳过 I/O。通常每镜1–3秒，仅重要情绪镜可到4秒，短场最多1个，全场20镜以上按5%上限。camera 写主体、焦段、机位、角度、高度、运镜、构图；content 以时长开头写完整分镜、表演与落幅，每条身体局部动作注明人物归属与左右。first/last 为本批单元 n，连续覆盖。长台词切反应镜后 continuation=true，first 等于上一镜 last。dialogueSlice 逐字分配原对白，注明说话人、画内/画外、接上镜/续下镜，标签放引文外。所有片段拼接还原原句。落实本场每项 requiredShots，在落实镜的 mustShotRefs 逐条引用其原文，在 content 中具体描写对应动作与必见结果。保留连戏锁，assets 只填实际入画资产 Id。\n" + AgentData.Json().Serialize(data);
                string correction = "", failed = "", lastError = "";
                var repairTime = new System.Diagnostics.Stopwatch();
                FlowRepairOutcome boardFix = null;
                for (int trial = 0;; trial++)
                {
                    try
                    {
                        var reply = boardFix != null ? AgentData.ParseOutput<BoardReply>(boardFix.RepairedText) : Ask<BoardReply>(exe, model, skill, vertical ? V2BoardSchema : BoardSchema, request + correction, token, progress, retryInvalidOutput:false);
                        failed = AgentData.Json().Serialize(reply);
                        if (reply.shots == null || reply.shots.Count == 0 || reply.shots[0] == null || reply.shots.Last() == null || reply.shots[0].first != 1 || reply.shots.Last().last != spans.Count)
                            throw new InvalidDataException("未覆盖本批完整原文范围。");
                        var local = new FilmScene
                        {
                            Cues = batch
                        };
                        for (int row = 0; row < reply.shots.Count; row++)
                        {
                            var sh = reply.shots[row];
                            if (sh == null || sh.first < 1 || sh.last < sh.first || sh.last > spans.Count)
                                throw new InvalidDataException("镜头单元编号超出本批范围。");
                            if (sh.assets != null)
                                sh.assets = sh.assets.Where(id => !String.IsNullOrWhiteSpace(id)).Select(id =>
                                {
                                    id=id.Trim();
                                    if (b.Assets.Any(a => a.Id == id)) return id;
                                    var named = b.Assets.Where(a => a.Name == id).Take(2).ToList();
                                    return named.Count == 1 ? named[0].Id : id;
                                }).Distinct().ToList();
                            if (vertical)
                            {
                                int index = shots.Count + row;
                                string suffix = ShotSuffix(index);
                                sh.code = NormalizeCode(sh.code);
                                sceneCode = NormalizeCode(sceneCode);
                                if (Regex.IsMatch(sceneCode,@"^\d+-\d+$") &&
                                    (sh.code == suffix || sh.code == sceneCode || sh.code == "" || sh.code == sceneCode.Split('-')[1] + suffix)) sh.code = sceneCode + suffix;
                                if (!Regex.IsMatch(sh.code ?? "", @"^\d+-\d+" + suffix + "$") ||
                                    (!String.IsNullOrWhiteSpace(sceneCode) && Regex.IsMatch(sceneCode, @"^\d+-\d+$") && sh.code != sceneCode + suffix))
                                    throw new InvalidDataException("晓羽镜号格式不符，当前镜号后缀应为 " + suffix);
                                if (String.IsNullOrWhiteSpace(sh.content))
                                    throw new InvalidDataException(sh.code + " 缺少分镜正文 content。");
                                if (sh.seconds > 4)
                                    throw new InvalidDataException(sh.code + " 单镜时长 seconds 超过 4 秒。");
                                if ((sh.continuation || spans.Skip(sh.first-1).Take(sh.last-sh.first+1).SelectMany(span => span).Any(c => Film.Spoken(c,b.Brief.Language))) && String.IsNullOrWhiteSpace(sh.dialogueSlice))
                                    throw new InvalidDataException(sh.code + " 有对白或续接镜缺少 dialogueSlice。");
                            }
                            sh.first = batch.IndexOf(sh.continuation ? spans[sh.first-1].Last() : spans[sh.first-1].First())+1;
                            sh.last = batch.IndexOf(spans[sh.last-1].Last())+1;
                        }

                        if (vertical && offset + batch.Count == s.Cues.Count)
                        {
                            CheckRequiredShots(shots.Concat(reply.shots).ToList(), requiredShots);
                        }
                        Film.CheckShots(b, local, reply.shots);
                        if(target!=null && String.IsNullOrEmpty(projectRoot) && reply.shots.Any(sh=>sh.seconds>target.Max || sh.assets.Distinct().Count()>target.Images))
                            throw new InvalidDataException("按所选平台分镜，每镜最多 "+target.Max+" 秒，最多 "+target.Images+" 张图。");
                        foreach (var sh in reply.shots)
                        {
                            sh.first += offset;
                            sh.last += offset;
                        }

                        RecordCorrection(projectRoot,model,3,s.Title,"FilmWriter.Board/Film.CheckShots",
                            FlowIncidentCategory.StoryboardValidation,lastError,boardFix,repairTime);
                        shots.AddRange(reply.shots);
                        break;
                    }
                    catch (InvalidDataException e)
                    {
                        repairTime.Start();
                        if (trial > 1 || (trial == 1 && String.IsNullOrEmpty(projectRoot))) throw;
                        if (trial == 1)
                        {
                            boardFix = FlowAutoRepair.ResolveStructuredJson(projectRoot,exe,model,skill,FlowHalf.PostAssets,3,"分镜",s.Title,
                                "FilmWriter.Board/Film.CheckShots",(vertical ? V2BoardSchema : BoardSchema).Replace('\'','"'),
                                e.Data["failedOutput"] as string ?? failed,e.Message,request,token,progress);
                            if (!boardFix.Succeeded) throw new InvalidDataException(e.Message + "；一次性修复：" + boardFix.RootCause);
                        }
                        lastError = e.Message;
                        correction = "\n上次校验失败，请纠正：" + lastError;
                    }
                }

                offset += batch.Count;
            }

            Film.CheckShots(b, s, shots);
            return shots;
        }

        internal static List<FilmAsset> Assets(FilmBook b, FilmShot shot)
        {
            return shot.assets.Distinct().Select(id => b.Assets.First(a => a.Id == id)).ToList();
        }

        internal static string SourceText(FilmCue c)
        {
            return (c.Speaker ?? "") + (String.IsNullOrEmpty(c.Voice) ? "" : "（" + c.Voice + "）") + "：“" + c.Text + "”";
        }

        private static object SignatureData(FilmBook b, FilmScene s, FilmSegment g)
        {
            var basis = SegmentSignatureData(b,s,g);
            return String.IsNullOrEmpty(s.GlobalPrompt) ? basis : new { basis, s.GlobalPrompt };
        }

        private static object SegmentSignatureData(FilmBook b, FilmScene s, FilmSegment g)
        {
            bool extended = b.FinalSkill != null || Film.Shots(s,g).Any(sh => sh.continuation || !String.IsNullOrEmpty(sh.code) || !String.IsNullOrEmpty(sh.content));
            if (extended)
                return new { s.Id, s.SourceHash, b.AnalysisId, b.DeliveryId, b.Brief.Style, b.Brief.Camera, b.Brief.Ratio, b.Brief.Language,
                    finalSkill = b.FinalSkill == null ? null : new { b.FinalSkill.Id, b.FinalSkill.Digest },
                    visual = VisualFacts(b, Film.Shots(s,g).SelectMany(sh => Assets(b,sh)), SegmentSpeakers(b,s,g)),
                    cues = s.Cues, g.First, g.Last, shots = Film.Shots(s,g).Select(sh => new { sh, refs = Assets(b,sh).Select(a => new { a.Id, a.Name, a.Look, a.Actor, a.Panels, im = Film.Image(a,sh.angle) }) }) };
            return LegacySignatureData(b,s,g);
        }

        private static object LegacySignatureData(FilmBook b, FilmScene s, FilmSegment g)
        {
            // Match the serialized pre-0.2 shot shape for existing prompt signatures.
            return new { s.Id, s.SourceHash, b.AnalysisId, b.DeliveryId, b.Brief.Style, b.Brief.Camera, b.Brief.Ratio, b.Brief.Language, cues = s.Cues, g.First, g.Last, shots = Film.Shots(s, g).Select(sh => new { sh = new { sh.first, sh.last, sh.seconds, sh.camera, sh.action, sh.end, sh.angle, sh.assets }, refs = Assets(b, sh).Select(a => new { a.Id, a.Name, a.Look, a.Actor, im = Film.Image(a, sh.angle) }) }) };
        }

        internal static string Signature(FilmBook b, FilmScene s, FilmSegment g)
        {
            return "v2-" + AgentData.Hash(AssetProduction.StableJson(SignatureData(b, s, g)));
        }

        internal static bool Matches(FilmBook b, FilmScene s, FilmSegment g, string saved)
        {
            if (String.IsNullOrWhiteSpace(saved)) return false;
            if (b.FinalSkill != null && CanvasIdentity(b.FinalSkill.Id + " " + b.FinalSkill.Name) &&
                (String.IsNullOrEmpty(s.GlobalPrompt) || s.GlobalPromptSignature != AgentData.Hash(AssetProduction.StableJson(SceneGlobalFacts(b,s,b.FinalSkill)))))
                return false;
            // Faithful alternatives share inputs; the suffix identifies the exact
            // generated text when an older editing control passes back only a signature.
            int split = saved.IndexOf('/');
            if (split >= 0) saved = saved.Substring(0, split);
            if (saved.StartsWith("v2-", StringComparison.Ordinal))
            {
                if (saved == Signature(b, s, g)) return true;
                // Earlier releases stored the chosen skill name but no digest.
                // Preserve that manuscript when its recorded source still matches.
                if (!String.IsNullOrEmpty(s.GlobalPrompt) || b.FinalSkill == null || !g.History.Concat(g.Candidates).Any(p =>
                    !p.SkillFaithful && p.Signature == saved && p.Skill == b.FinalSkill.Name)) return false;
                return saved == "v2-" + AgentData.Hash(AssetProduction.StableJson(LegacySignatureData(b,s,g)));
            }
            // The original Framework serializer can enumerate members differently on its first pass.
            var data = SignatureData(b, s, g);
            return saved == Film.Hash(data) || saved == Film.Hash(data);
        }

        internal static List<PromptReference> References(FilmBook b, FilmScene s, FilmSegment g)
        {
            var result = new List<PromptReference>();
            var labels = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sh in Film.Shots(s,g))
                foreach (var a in Assets(b,sh))
                {
                    var img = Film.Image(a,sh.angle);
                    if (img == null) continue;
                    string label;
                    if (!labels.TryGetValue(img.Digest,out label))
                        labels.Add(img.Digest,label = "@Image" + (labels.Count+1));
                    var item = new PromptReference { Label=label, Name=a.Name, Identity=a.Identity, Angle=img.Angle, Look=a.Look,
                        Actor=a.Actor, Path=img.Path, Digest=img.Digest };
                    if (!result.Any(r => r.Caption == item.Caption)) result.Add(item);
                }
            return result;
        }

        internal static string BindVisualReferences(string direction,List<PromptReference> references)
        {
            var labels=references.Where(r=>!String.IsNullOrWhiteSpace(r.Name)).GroupBy(r=>r.Name)
                .Where(g=>g.Select(r=>r.Label).Distinct().Count()==1).ToDictionary(g=>g.Key,g=>g.First().Label);
            direction=ProductionRules.Prose(direction,references);
            if(labels.Count==0)return direction;
            string pattern=String.Join("|",labels.Keys.OrderByDescending(n=>n.Length).Select(n=>
                (Regex.IsMatch(n,@"^[a-zA-Z0-9]")?@"(?<![a-zA-Z0-9_])":"")+Regex.Escape(n)+
                (Regex.IsMatch(n,@"[a-zA-Z0-9]$")?@"(?![a-zA-Z0-9_])":"")));
            return ProductionRules.Prose(Regex.Replace(direction,pattern,m=>
                Regex.IsMatch(direction.Substring(m.Index+m.Length),@"^\s*@Image\d+") ? m.Value : m.Value+" "+labels[m.Value]),references);
        }

        internal static List<string> Requirements(FilmBook b, FilmScene s, FilmSegment g)
        {
            var lines = new List<string>();
            var references = References(b,s,g);
            if (!String.IsNullOrWhiteSpace(b.Brief.Style))
                lines.Add("全片风格：" + b.Brief.Style);
            if (!String.IsNullOrWhiteSpace(b.Brief.Camera))
                lines.Add("摄影：" + b.Brief.Camera);
            int t = 0;
            foreach (var sh in Film.Shots(s, g))
            {
                lines.Add(t + "–" + (t + sh.seconds) + " 秒");
                t += sh.seconds;
                foreach (var a in Assets(b, sh))
                {
                    var im = Film.Image(a, sh.angle);
                    if (im == null)
                        continue;
                    var label = references.First(r => r.Name == a.Name && r.Digest == im.Digest && r.Angle == im.Angle && r.Look == a.Look && r.Actor == a.Actor).Caption;
                    if (!lines.Contains(label))
                        lines.Add(label);
                }

                foreach (var c in Film.Covered(s, sh))
                {
                    if (Film.Spoken(c, b.Brief.Language))
                    {
                        if (!String.IsNullOrEmpty(c.Note))
                            lines.Add("表演：" + c.Note);
                        lines.Add(SourceText(c));
                    }
                    else if (c.Type == "对白" && c.Language != null)
                    {
                        string performance = String.Join(" ",new[]{c.Voice,c.Note}.Where(x=>!String.IsNullOrWhiteSpace(x)));
                        if (performance.Length > 0) lines.Add("表演：" + performance);
                    }
                }
            }

            return lines.Select(line=>ProductionRules.Body(line,references)).Where(line=>line.Length>0).ToList();
        }

        internal static string[] Lost(FilmBook b, FilmScene s, FilmSegment g, string text)
        {
            if (g.Candidates.Concat(g.History).Any(p => p.SkillFaithful && p.Text == text))
                return VideoPromptFidelity.Validate(b,s,g,text);
            return LostDefault(b,s,g,text);
        }

        private static string[] LostDefault(FilmBook b, FilmScene s, FilmSegment g, string text)
        {
            try { text=ProductionRules.Body(text,References(b,s,g)); }
            catch (InvalidDataException issue) { return new[]{issue.Message}; }
            int cursor = 0;
            var missing = new List<string>();
            foreach (string item in Requirements(b, s, g))
            {
                int index = (text ?? "").IndexOf(item, cursor, StringComparison.Ordinal);
                if (index < 0)
                    missing.Add(item);
                else
                    cursor = index + item.Length;
            }

            var expected = Requirements(b, s, g);
            foreach (Match m in Regex.Matches(text ?? "", @"@Image\d+"))
                if (!expected.Any(x => x.StartsWith(m.Value + " ", StringComparison.Ordinal)))
                    missing.Add("未绑定素材 " + m.Value);
            var said = Film.Shots(s, g).SelectMany(sh => Film.Covered(s, sh)).Where(c => Film.Spoken(c, b.Brief.Language)).Select(SourceText).ToList();
            foreach (var group in said.GroupBy(x => x))
            {
                int count = Regex.Matches(text ?? "", Regex.Escape(group.Key)).Count;
                if (count > group.Count())
                    missing.Add("重复对白 " + group.Key);
            }

            foreach (string line in (text ?? "").Split('\n').Select(x => x.Trim()))
                if (Regex.IsMatch(line, @"^[\p{L}][^：:\n]{0,60}[:：][“""].+[”""]$") && !said.Contains(line) && !expected.Contains(line))
                    missing.Add("未核对说话人或新增对白 " + line);
            var direction = String.Join("\n", (text ?? "").Split('\n').Select(line=>line.TrimEnd('\r')).Except(expected));
            var conflict = Film.VoiceConflict(b.Brief.Language, direction);
            var sourceCues = Film.Shots(s,g).SelectMany(sh=>Film.Covered(s,sh)).ToList();
            if (b.Brief.Language == "原文" && sourceCues.Any(c=>c.Language=="中文") && sourceCues.Any(c=>c.Language=="英文") &&
                (Film.VoiceConflict("英文",direction)!=null || Film.VoiceConflict("中文",direction)!=null))
                missing.Add("双语原稿的镜头说明指定了单一配音语言，请核对旁白。");
            foreach (var omitted in sourceCues.Where(c=>c.Type=="对白" && !Film.Spoken(c,b.Brief.Language)))
            {
                var quote = (omitted.Text ?? "").Trim();
                if (quote.Length>0 && !sourceCues.Any(c=>Film.Spoken(c,b.Brief.Language) && c.Text==quote) &&
                    (direction.Contains("“"+quote+"”") || direction.Contains("「"+quote+"」") || direction.Contains("\""+quote+"\"")))
                    missing.Add("画面说明重复加入了译文：" + quote);
            }
            if (conflict != null) missing.Add(conflict);
            if (Film.ProtectedBoundary(s,g.First-1) || Film.ProtectedBoundary(s,g.Last))
                missing.Add("片段边界拆开同一句的双语版本。");
            return missing.ToArray();
        }

        internal static string[] PromptIssues(FilmBook b, FilmScene s, FilmSegment g, FilmPrompt prompt)
        {
            if (prompt.SkillFaithful && CanvasIdentity(prompt.SkillId + " " + prompt.Skill))
            {
                try { CheckCanvas(b,s,g,prompt.Text); }
                catch (InvalidDataException issue) { return new[] { issue.Message }; }
            }
            return prompt.SkillFaithful ? VideoPromptFidelity.Validate(b,s,g,prompt.Text) : LostDefault(b,s,g,prompt.Text);
        }

        private static IEnumerable<string> SegmentSpeakers(FilmBook book, FilmScene scene, FilmSegment segment)
        {
            return Film.Shots(scene, segment).SelectMany(sh => Film.Covered(scene, sh))
                .Where(c => Film.Spoken(c, book.Brief.Language)).Select(c => c.Speaker).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameCategory(string actual, string expected)
        {
            if (actual == expected) return true;
            if (expected == "场景") return actual == "地点";
            if (expected == "人物") return actual == "角色";
            return false;
        }

        private static List<string> CanvasNames(FilmBook b, FilmScene s, FilmSegment g, string category)
        {
            var names = Film.Shots(s,g).SelectMany(sh => Assets(b,sh)).Where(a => SameCategory(a.Category,category)).Select(a => a.Name).Distinct();
            var refs = References(b,s,g);
            return names.SelectMany(name => {
                var angles = refs.Where(r => r.Name == name).GroupBy(r => r.Label).Select(group => group.First().Angle).ToArray();
                return angles.Length > 1 ? angles.Select(angle => name + "（" + angle + "）") : new[] { name };
            }).ToList();
        }

        private static string CanvasHeader(FilmBook b, FilmScene s, FilmSegment g)
        {
            string global = (s.GlobalPrompt ?? "").Replace("\r\n","\n");
            int first = global.IndexOf('\n');
            if (first < 0) throw new InvalidDataException("请先生成本场全局提示词。");
            var lines = new List<string> { global.Substring(0,first) };
            foreach (string category in new[] { "场景", "人物", "道具" })
            {
                var names = CanvasNames(b,s,g,category);
                if (category == "道具" && names.Count == 0) continue;
                lines.Add("全局" + category + "参考：" + (names.Count == 0 ? "无" : String.Join("；",names.Select(n => n + "参考  "))));
            }
            return String.Join("\n",lines) + global.Substring(first);
        }

        private static object SegmentFacts(FilmBook b, FilmScene s, FilmSegment g, int number, bool canvas)
        {
            var refs = References(b,s,g);
            var segmentAssets = new List<object>();
            int elapsed = 0;
            var shots = Film.Shots(s,g).Select(sh =>
            {
                int start = elapsed;
                elapsed += sh.seconds;
                return new { sh.code, startSeconds = start, endSeconds = elapsed, sh.camera, sh.content, sh.action, sh.end,
                    sh.continuation, sh.dialogueSlice, sh.mustShotRefs,
                    assets = Assets(b,sh).Select(a =>
                    {
                        var im = Film.Image(a,sh.angle);
                        var reference = im == null ? null : refs.First(r => r.Digest == im.Digest);
                        string label = reference == null ? null : reference.Label;
                        var detail = new { name = a.Name, identity = a.Identity, label,
                            angle = im == null ? sh.angle : im.Angle, look = a.Look, panels = a.Panels };
                        if (!segmentAssets.Contains(detail)) segmentAssets.Add(detail);
                        return new { name = a.Name, label };
                    }).ToArray(),
                    dialogue = Film.Covered(s,sh).Where(c => Film.Spoken(c,b.Brief.Language)).Select(c => new { c.Speaker, c.Text, c.Voice, c.Note })
                };
            }).ToList();
            if (canvas)
                return new { number, scene = s.Title, shots, totalSeconds = elapsed, canvas = b.Brief.Ratio, language = b.Brief.Language,
                    segmentAssets, requiredHeader = CanvasHeader(b,s,g),
                    visualSettings = VisualFacts(b, Film.Shots(s,g).SelectMany(sh => Assets(b,sh)), SegmentSpeakers(b,s,g)) };
            return new { number, scene = s.Title, shots, segmentAssets, totalSeconds = elapsed,
                canvas = b.Brief.Ratio, language = b.Brief.Language, style = b.Brief.Style, photography = b.Brief.Camera,
                visualSettings = VisualFacts(b, Film.Shots(s,g).SelectMany(sh => Assets(b,sh)), SegmentSpeakers(b,s,g)) };
        }

        private static string CanvasLineKey(string line)
        {
            string trimmed = (line ?? "").TrimEnd(' ', '\t');
            if (Regex.IsMatch(trimmed, @"^全局(?:场景|人物|角色|道具)参考[：:]"))
                trimmed = Regex.Replace(trimmed, @"参考[ \t]*(?=[；;]|$)", "参考");
            return trimmed;
        }

        internal static string NormalizeCanvas(FilmBook b, FilmScene s, FilmSegment g, string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            string header = CanvasHeader(b, s, g);
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = Regex.Replace(normalized,@"(?m)^([ \t]*)(\d+)\s*[-–—]\s*(\d+)[ \t]*秒[.。]","$1$2-$3秒。");
            normalized = Regex.Replace(normalized,@"(?m)(^|[ \t。])(画面|动作/表情|情感解析|声音):","$1$2：");
            if (!normalized.TrimEnd().EndsWith("\n无配乐无字幕",StringComparison.Ordinal)) normalized = normalized.TrimEnd() + "\n无配乐无字幕";
            normalized = Regex.Replace(normalized, @"\n无配乐无字幕[ \t]*\n*$", "\n无配乐无字幕");
            if (normalized.StartsWith(header + "\n", StringComparison.Ordinal))
                return normalized == text ? text : normalized;
            var expected = header.Split('\n');
            var actual = normalized.TrimStart('\n').Split('\n');
            if (actual.Length <= expected.Length) return text;
            for (int i = 0; i < expected.Length; i++)
                if (CanvasLineKey(actual[i]) != CanvasLineKey(expected[i])) return text;
            return header + "\n" + String.Join("\n", actual.Skip(expected.Length));
        }

        private static void CheckCanvas(FilmBook b, FilmScene s, FilmSegment g, string text)
        {
            string body = (text ?? "").Replace("\r\n","\n").Replace('\r','\n').TrimEnd('\n');
            string header = CanvasHeader(b,s,g);
            if (!body.StartsWith(header + "\n",StringComparison.Ordinal) || !body.EndsWith("\n无配乐无字幕",StringComparison.Ordinal))
                throw new InvalidDataException("请逐字复制 requiredHeader（保留参考后的两个空格），并以独立一行无配乐无字幕结尾。");
            var matches = Regex.Matches(body.Substring(header.Length),@"(?m)^[ \t]*(\d+)\s*[-–—]\s*(\d+)\s*秒。([^\n]*)");
            var shots = Film.Shots(s,g).ToList();
            if (matches.Count != shots.Count) throw new InvalidDataException("请按镜数逐镜以 X-Y秒。与摄影机说明开头。");
            int elapsed = 0;
            string timeline = body.Substring(header.Length);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int end = elapsed + shots[i].seconds;
                int stop = i + 1 == matches.Count ? timeline.LastIndexOf("\n无配乐无字幕",StringComparison.Ordinal) : matches[i+1].Index;
                string shot = timeline.Substring(m.Index,stop-m.Index);
                if (m.Groups[1].Value != elapsed.ToString() || m.Groups[2].Value != end.ToString() ||
                    !Regex.IsMatch(shot,@"画面[：:]\s*\S[\s\S]*?动作\s*/\s*表情[：:]\s*\S[\s\S]*?情感解析[：:]\s*\S[\s\S]*?声音[：:]\s*\S"))
                    throw new InvalidDataException("请核对第 " + (i+1) + " 镜的时段与画面、动作/表情、情感解析、声音四项正文。");
                elapsed = end;
            }
        }

        internal static bool Current(FilmBook b, FilmScene s, FilmSegment g)
        {
            return g.Accepted >= 0 && g.Accepted < g.History.Count && Matches(b, s, g, g.History[g.Accepted].Signature) && PromptIssues(b, s, g, g.History[g.Accepted]).Length == 0;
        }

        internal static List<FilmPrompt> Write(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s, IList<FilmSegment> groups, string notes, bool alternatives, CancellationToken token, Action<string> progress)
        {
            return WriteForFlow(exe,model,skill,b,s,groups,notes,alternatives,token,progress,null);
        }

        internal static List<FilmPrompt> WriteForFlow(string exe, string model, SelectedSkill skill, FilmBook b, FilmScene s, IList<FilmSegment> groups, string notes, bool alternatives, CancellationToken token, Action<string> progress, string projectRoot)
        {
            bool canvas = IsOneClickPromptSkill(skill);
            if (canvas) skill = BuiltinSkills.WithPreloadedReferences(skill,"references/scene-canvas.md","references/canvas-example.md");
            int count = alternatives ? 3 : groups.Count;
            string cmd;
            if (skill != null)
            {
                cmd = Film.LanguageRule(b.Brief.Language) + (canvas ?
                    "Stage 3 单段画布。" + (skill != null && skill.PreloadedReferences ? "按已预载的 scene-canvas.md 与 canvas-example.md 规则（无需调用工具）" : "按 references/scene-canvas.md 与 references/canvas-example.md ") + "交付完整可粘贴正文，不询问也不运行 export-canvas.mjs 或 Python。" +
                    "开头逐字复制本段 requiredHeader，包括参考项后的两个半角空格、光影、环境、群演、声线与固定尾句。随后每镜从本段 0 秒起按 startSeconds-endSeconds秒。和摄影机起头，" +
                    "依次写画面：、动作/表情：、情感解析：、声音：（无则声音：N/A）。稳态光影留在全局，仅开关灯等剧情动作入镜。用画面，不用竖幅/横幅。" +
                    "按段内镜序保留原语言原台词与说话人，跨镜按 dialogueSlice 续接，同一原句勿重复。末行逐字写无配乐无字幕。" :
                    "严格按所选 Skill 的完整结构和排版，使用本段制作事实和真实参考图，按镜序保持原台词与动作。direction 直接交付可独立复制的完整提示词全文；若有时间段，将对应动作写在时段内。保留 Skill 所需素材、音色或站位标注，优先使用完整资产名，如【此处@林澈 · 白色校服】。")
                    + (alternatives ? "对同一片段给出三种拍法，编号1至3。" : "各片段各返回一个对应编号的 direction。")
                    + "\n导演批注：" + notes + "\n分段制作资料：" + AgentData.Json().Serialize(groups.Select((g,i) => SegmentFacts(b,s,g,i+1,canvas)));
            }
            else
            {
                var input = groups.Select((g, i) => new { number = i + 1, scene = s.Title, referenceNotes = Film.Shots(s,g).SelectMany(sh=>Assets(b,sh)).Distinct().Select(a=>new { a.Name, a.Scope, a.Panels }), shots = Film.Shots(s, g).Select(sh => new { sh.action, sh.camera, sh.seconds, sh.end, assets=Assets(b,sh).Select(a=>a.Name) }), required = Requirements(b, s, g), canvas = b.Brief.Ratio, style = b.Brief.Style, photography = b.Brief.Camera });
                cmd = Film.LanguageRule(b.Brief.Language) + "使用多状态或多格参考图须按分镜交代所用格和适用镜头，不确定时注明待核对。为 "+(b.VideoModelName??"Seedance 2.5")+" 写可直接使用的逐段画面提示词。按镜序用中文写可见动作、人物表演、空间和运镜。直接从正文开始，不要片名、小节标题、SHOT编号、成片画幅或此处@占位符。对白由应用逐字追加，direction 不重复写台词或输出参数。在画面说明中使用本镜实际入画的资产名；应用按本段参考图清单绑定编号，请勿自行写编号。不可增加人物、事件或对白；任何情况下均不可设计字幕、画面叠字、时间字卡或后期字幕。危险动作使用画外声、反应镜头及安全表演呈现。" + (alternatives ? "对这同一段给三种不同的拍法，分别偏重构图、光线、运动，编号1至3。" : "每个给定编号只交一个 direction。") + "\n导演批注：" + notes + "\n分镜与原文：" + AgentData.Json().Serialize(input);
            }
            var repairTime = new System.Diagnostics.Stopwatch();
            FlowRepairOutcome fix = null;
            string failed = "", error = "";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var reply = fix == null ? Ask<PromptReply>(exe, model, skill, TextSchema, cmd, token, progress, retryInvalidOutput:false) :
                        count == 1 ? new PromptReply { pieces=new List<PromptPiece>{ new PromptPiece { number=1,direction=fix.RepairedText } } } :
                        AgentData.ParseOutput<PromptReply>(fix.RepairedText);
                    failed = AgentData.Json().Serialize(reply);
                    if (reply.pieces == null || reply.pieces.Count != count || reply.pieces.Any(p => p == null) || !reply.pieces.Select(p => p.number).OrderBy(n => n).SequenceEqual(Enumerable.Range(1, count)))
                        throw new InvalidDataException("提示词回执的段数或编号不匹配，本场可以重试。");
                    var prompts = reply.pieces.OrderBy(q => q.number).Select((q, i) =>
                    {
                        var g = alternatives ? groups[0] : groups[i];
                        string direction = canvas ? NormalizeCanvas(b, s, g, q.direction) : q.direction;
                        if (canvas) CheckCanvas(b, s, g, direction);
                        if (skill != null)
                            return new FilmPrompt { Text = direction, RawText = q.direction, SkillFaithful = true,
                                Signature = Signature(b,s,g) + "/" + AgentData.Hash(direction ?? ""), Skill = skill.Reference.Name, SkillId = skill.Reference.Id,
                                SkillDigest = skill.Reference.Digest, Utc = AnalysisData.Now() };
                        if (q.direction != null && Regex.IsMatch(q.direction,@"@Image\s*\d+",RegexOptions.IgnoreCase))
                            throw new InvalidDataException("画面说明自行指定了图片编号。请使用资产名，编号由应用绑定。");
                        if (String.IsNullOrWhiteSpace(ProductionRules.Body(q.direction,References(b,s,g))) || Regex.IsMatch(q.direction ?? "",@"字幕|字卡|subtitles?",RegexOptions.IgnoreCase))
                            throw new InvalidDataException("请提供完整画面正文，删除字幕或字卡描述。");
                        return new FilmPrompt
                        {
                            Text = ProductionRules.Body(BindVisualReferences(q.direction.Trim(),References(b,s,g)),References(b,s,g)) + "\n" + String.Join("\n", Requirements(b, s, g)),
                            Signature = Signature(b, s, g),
                            Skill = "",
                            Utc = AnalysisData.Now()
                        };
                    }).Select((p,i) => {
                        var segment = alternatives ? groups[0] : groups[i];
                        var issues = p.SkillFaithful ? VideoPromptFidelity.Validate(b,s,segment,p.Text) : LostDefault(b,s,segment,p.Text);
                        if (issues.Length > 0) throw new InvalidDataException(String.Join("、",issues));
                        return p;
                    }).ToList();
                    RecordCorrection(projectRoot,model,4,s.Title,"FilmWriter.Write/CheckCanvas/VideoPromptFidelity",
                        FlowIncidentCategory.FinalPromptValidation,error,fix,repairTime);
                    return prompts;
                }
                catch (InvalidDataException ex)
                {
                    repairTime.Start();
                    error=ex.Message;
                    if (attempt > 1 || (attempt == 1 && String.IsNullOrEmpty(projectRoot))) throw;
                    if (attempt == 1)
                    {
                        string raw = ex.Data["failedOutput"] as string ?? failed;
                        const string hint = "FilmWriter.Write/CheckCanvas/VideoPromptFidelity";
                        fix = count == 1
                            ? FlowAutoRepair.ResolveTextIssue(projectRoot,exe,model,skill,FlowHalf.PostAssets,4,"最终提示词",s.Title,
                                hint,cmd + "\n只返回该段完整 direction 正文。",error + "\n失败稿：" + raw,token,progress)
                            : FlowAutoRepair.ResolveStructuredJson(projectRoot,exe,model,skill,FlowHalf.PostAssets,4,"最终提示词",s.Title,
                                hint,TextSchema.Replace('\'','"'),raw,error,cmd,token,progress);
                        if (!fix.Succeeded) throw new InvalidDataException(error + "；" + fix.RootCause);
                    }
                    cmd += "\n请修正上次的核对问题：" + ex.Message;
                }
            }
        }

        internal static void Accept(FilmBook b, FilmScene s, FilmSegment g, FilmPrompt prompt)
        {
            if (!s.Grouped || !s.Approved || !Matches(b, s, g, prompt.Signature))
                throw new InvalidOperationException("上游已修改，请按当前分镜重新核对和生成。");
            // Older editing controls return a new text-only object. Recover its
            // generation provenance from this segment before validating the edit.
            if (!prompt.SkillFaithful && !String.IsNullOrEmpty(prompt.Skill) && !g.Candidates.Contains(prompt))
            {
                FilmPrompt basis = null;
                if (prompt.Skill == "人工修订")
                    basis = g.Accepted >= 0 && g.Accepted < g.History.Count ? g.History[g.Accepted] : g.Candidates.FirstOrDefault();
                else
                {
                    var choices = g.Candidates.Where(p => p.Signature == prompt.Signature && p.Skill == prompt.Skill).ToList();
                    basis = choices.FirstOrDefault();
                    // Multiple alternatives have the same input fingerprint. Preserve all
                    // originals in Candidates without attributing the wrong raw response.
                    if (choices.Count == 1) prompt.RawText = basis.RawText;
                }
                if (basis != null && basis.SkillFaithful)
                {
                    if (!Matches(b,s,g,basis.Signature)) throw new InvalidDataException("上游或 Skill 已更改，请生成新的候选稿。");
                    if (prompt.Skill == "人工修订") { prompt.RawText = basis.RawText; prompt.Signature = basis.Signature; }
                    prompt.Skill = basis.Skill; prompt.SkillId = basis.SkillId;
                    prompt.SkillDigest = basis.SkillDigest; prompt.SkillFaithful = true;
                }
            }
            prompt.Text=VideoPromptFidelity.DisplayText(prompt,References(b,s,g));
            if (prompt.SkillFaithful && CanvasIdentity(prompt.SkillId + " " + prompt.Skill))
                try { prompt.Text = NormalizeCanvas(b, s, g, prompt.Text); } catch (InvalidDataException) { }
            var missing = PromptIssues(b, s, g, prompt);
            if (String.IsNullOrWhiteSpace(prompt.Text) || missing.Length > 0)
                throw new InvalidDataException("以下原句或引用缺失或次序不符：\n" + String.Join("\n", missing));
            g.History.Add(new FilmPrompt { Text = prompt.Text, Signature = prompt.Signature, Skill = prompt.Skill, RawText = prompt.RawText, SkillId = prompt.SkillId, SkillDigest = prompt.SkillDigest, SkillFaithful = prompt.SkillFaithful, References = References(b,s,g), Utc = AnalysisData.Now() });
            g.Accepted = g.History.Count - 1;
        }

        private static string Csv(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static string Md(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        }

        internal static string Export(string root, FilmBook b, bool includePrompts)
        {
            var scenes = b.Scenes.Where(s => s.Included).ToList();
            if (scenes.Count == 0)
                throw new InvalidDataException("尚未选择要制作的场次。");
            var csv = new StringBuilder();
            var md = new StringBuilder();
            var timeline = new List<object>();
            int time = 0;
            var headers = new List<string>
            {
                "场次",
                "片段",
                "镜号",
                "原文行",
                "时长（秒）",
                "摄影",
                "动作",
                "末态",
                "人物、地点与素材",
                "对白"
            };
            if (includePrompts)
                headers.Add("片段提示词");
            csv.AppendLine(String.Join(",", headers.Select(Csv)));
            md.AppendLine("# " + b.Brief.Title + " · 镜头清单\n").AppendLine("|" + String.Join("|", headers) + "|").AppendLine("|" + String.Join("|", headers.Select(h => "---")) + "|");
            foreach (var s in scenes)
            {
                Film.RequireReady(root, b, s);
                if (!s.Approved || (includePrompts && (!s.Grouped || s.Segments.Count == 0)))
                    throw new InvalidOperationException(s.Title + " 缺少分镜或分段审核。");
                var groups = s.Grouped ? s.Segments : Film.Group(s, 30);
                foreach (var g in groups)
                {
                    if (includePrompts && !Current(b, s, g))
                        throw new InvalidOperationException(s.Title + " 第 " + (s.Segments.IndexOf(g) + 1) + " 段缺少已采纳的当前提示词。");
                    var prompt = includePrompts ? g.History[g.Accepted] : null;
                    var references = References(b,s,g);
                    int start = time;
                    for (int i = g.First; i <= g.Last; i++)
                    {
                        var sh = s.Shots[i];
                        var refs = Assets(b, sh).Select(a => new { a.Name, img = Film.Image(a, sh.angle) }).ToList();
                        var cues = Film.Covered(s, sh).ToList();
                        var row = new List<string>
                        {
                            s.Title,
                            (groups.IndexOf(g) + 1).ToString(),
                            sh.code ?? (i + 1).ToString(),
                            s.Cues[sh.first-1].First + "–" + s.Cues[sh.last-1].Last,
                            sh.seconds.ToString(),
                            sh.camera,
                            sh.content ?? sh.action,
                            sh.end,
                            String.Join("、", refs.Select(r => r.Name + " · " + r.img.Angle)),
                            !String.IsNullOrWhiteSpace(sh.dialogueSlice) ? sh.dialogueSlice : String.Join("\n", cues.Where(c => Film.Spoken(c, b.Brief.Language)).Select(SourceText))
                        };
                        if (includePrompts)
                            row.Add(VideoPromptFidelity.DisplayText(prompt,references));
                        md.AppendLine("|" + String.Join("|", row.Select(Md)) + "|");
                        csv.AppendLine(String.Join(",", row.Select(Csv)));
                        time += sh.seconds;
                    }

                    if (includePrompts)
                        timeline.Add(new { scene = s.Id, title = s.Title, firstShot = g.First + 1, lastShot = g.Last + 1, startSeconds = start, endSeconds = time, model = b.VideoModelName ?? "Seedance 2.5", aspectRatio = b.Brief.Ratio, prompt = VideoPromptFidelity.DisplayText(prompt,references), references = references.GroupBy(r => r.Label).Select(set => new { label = set.Key, name = String.Join("、",set.Select(r=>r.Name).Distinct()), projectPath = set.First().Path, sha256 = set.First().Digest }), videoStatus = "待生成" });
                }
            }

            var exports = AgentData.Inside(root, @"影片制作\导出");
            Directory.CreateDirectory(exports);
            var dir = Directory.CreateDirectory(Path.Combine(exports, ".tmp-" + Film.Id())).FullName;
            try
            {
                File.WriteAllText(Path.Combine(dir, "镜头清单.csv"), csv.ToString(), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(dir, "镜头清单.md"), md.ToString(), new UTF8Encoding(true));
                if (includePrompts)
                    AgentData.Save(Path.Combine(dir, "Seedance 2.5 交接清单.json"), new { analysis = b.AnalysisId, assets = b.DeliveryId, videoModel=b.VideoModelName, totalSeconds = time, segments = timeline, missingVideos = timeline.Count });
                var fresh = Film.Open(root);
                if (fresh.Revision != b.Revision || fresh.AnalysisId != b.AnalysisId || fresh.DeliveryId != b.DeliveryId)
                    throw new IOException("项目在导出期间已变化，请刷新并重新导出。");
                var dest = Path.Combine(exports, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Film.Id().Substring(0, 6));
                Directory.Move(dir, dest);
                return dest;
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }
    }
}
