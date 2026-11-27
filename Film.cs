using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    public sealed class FilmBrief
    {
        public string Title, Idea, Style, Camera;
        public string Ratio = "16:9", Language = "原文";
        public int Target, Page = 2;
    }

    public sealed class FilmCue
    {
        public string Id, Speaker, Voice, Text, Type, Language, Note;
        public int First, Last;
    }

    public sealed class FilmImage
    {
        public string Angle, Path, Digest, Parent;
    }

    public sealed class FilmAsset
    {
        internal string Scope, Panels;
        internal List<int> Evidence;
        public string Id, Name, Category, Identity, Look, Actor, PendingImage, PendingAngle, PendingParent, PendingTargetSize;
        public List<FilmImage> Images = new List<FilmImage>();
    }

    public sealed class FilmShot
    {
        public int first, last, seconds;
        public string camera, action, end, angle;
        public string code, content, dialogueSlice;
        public bool continuation;
        public List<string> assets = new List<string>();
        public List<string> mustShotRefs = new List<string>();
    }

    public sealed class PromptReference
    {
        public string Label, Name, Identity, Angle, Look, Actor, Path, Digest;
        internal string Caption { get { return Label + " " + Name + " · " + Angle +
            (String.IsNullOrWhiteSpace(Look) ? "" : " · " + Look) + (String.IsNullOrWhiteSpace(Actor) ? "" : " · " + Actor); } }
    }

    public sealed class FilmPrompt
    {
        public string Text, Utc, Signature, Skill;
        public string RawText, SkillId, SkillDigest;
        public bool SkillFaithful;
        public List<PromptReference> References;
    }

    public sealed class FilmSegment
    {
        public int First, Last;
        public List<FilmPrompt> History = new List<FilmPrompt>();
        public List<FilmPrompt> Candidates = new List<FilmPrompt>();
        public int Accepted = -1;
    }

    public sealed class FilmScene
    {
        public string Id, Title, SourceHash, Status, LastError, BoardSkill;
        public string DirectorSummary, EpisodeBeats;
        public string GlobalPrompt, GlobalPromptSignature;
        public List<string> MustShots = new List<string>();
        public int First, Last;
        internal bool ProductionExcluded;
        public bool Included = true, Approved, Grouped;
        public int CueFormat;
        public List<FilmCue> Cues = new List<FilmCue>();
        public List<FilmShot> Shots = new List<FilmShot>();
        public List<FilmSegment> Segments = new List<FilmSegment>();
    }

    public sealed class FilmBook
    {
        internal bool SyncRequired;
        internal string RatioMismatchNote;
        internal string Background;
        internal bool V2Builtin;
        internal DirectorAnalysisDoc Director;
        internal VisualSettingDoc Visual;
        internal SkillReference FinalSkill;
        internal string VideoModelName;
        public string AnalysisId, DeliveryId, Revision;
        public FilmBrief Brief = new FilmBrief();
        public List<FilmAsset> Assets = new List<FilmAsset>();
        public List<FilmScene> Scenes = new List<FilmScene>();
    }

    internal static class Film
    {
        internal static readonly string[] Angles =
        {
            "主图",
            "侧面",
            "斜侧",
            "广景",
            "反打"
        };
        internal static IEnumerable<string> Continuity(FilmBook book, FilmScene scene)
        {
            var prior = new Dictionary<string, FilmAsset>(StringComparer.Ordinal);
            for (int i = 0; i < scene.Shots.Count; i++)
                foreach (string id in scene.Shots[i].assets)
                {
                    var a = book.Assets.FirstOrDefault(x => x.Id == id);
                    if (a == null || a.Category != "人物" || String.IsNullOrWhiteSpace(a.Identity))
                        continue;
                    FilmAsset old;
                    if (prior.TryGetValue(a.Identity, out old) && old.Id != a.Id)
                        yield return "镜 " + (i + 1) + " · " + a.Identity + " 的参考由「" + old.Name + "」切换为「" + a.Name + "」，请核对剧本是否在此换装或改变状态。";
                    prior[a.Identity] = a;
                }
        }

        private static string Record(string root)
        {
            return AgentData.Inside(root, @"影片制作\制作记录.json");
        }

        internal static string Clean(string text)
        {
            return Regex.Replace((text ?? "").Normalize(NormalizationForm.FormKC), @"[\p{Cc}]+", " ").Trim();
        }

        internal static string Hash(object obj)
        {
            return AnalysisData.Digest(Encoding.UTF8.GetBytes(AgentData.Json().Serialize(obj)));
        }

        internal static string Id()
        {
            return AgentData.Id();
        }

        internal static FilmBook Open(string root)
        {
            var book = AgentData.Read<FilmBook>(Record(root)) ?? new FilmBook
            {
                Brief = new FilmBrief
                {
                    Title = Path.GetFileName(root)
                }
            };
            var videoConfig=FlowCreation.Config(root);
            if (FlowCreation.IsFlowProject(root) && videoConfig != null && !String.IsNullOrWhiteSpace(videoConfig.Ratio) &&
                book.Brief.Ratio != videoConfig.Ratio)
            {
                if (VideoDelivery.ReceiptFiles(root).Any())
                    book.RatioMismatchNote = "一键成片配置画幅（" + videoConfig.Ratio + "）与制作记录画幅（" + book.Brief.Ratio +
                        "）不一致。已有平台回执，已保留原记录画幅。请核对原任务，暂时无法提交新视频。";
                else
                {
                    book.Brief.Ratio = videoConfig.Ratio;
                    book.SyncRequired = true;
                }
            }
            if(videoConfig!=null && videoConfig.Video!=null && videoConfig.Video.Enabled)book.VideoModelName=videoConfig.Video.Model;
            book.V2Builtin = videoConfig != null && videoConfig.IsV2Builtin;
            var source = AnalysisData.CurrentScript(root);
            if (source != null && source.ProjectLanguage != null) book.Brief.Language = source.ProjectLanguage;
            if (source == null)
            {
                book.Scenes.Clear();
                book.Assets.Clear();
                book.AnalysisId = null;
                book.DeliveryId = null;
                return book;
            }

            var version = AnalysisData.Load(root).Confirmed(source);
            if (version == null)
            {
                book.AnalysisId = null;
                book.DeliveryId = null;
                book.Scenes.Clear();
                book.Assets.Clear();
                return book;
            }

            book.Director = DirectorPipeline.LoadDirectorNotes(root, version.Id);
            book.Visual = DirectorPipeline.LoadVisualSettings(root, version.Id);
            book.FinalSkill = LocalSkills.Choice(root, LocalSkills.StageKey(4));
            if (book.FinalSkill != null && !(book.FinalSkill.Id ?? "").StartsWith("flow:", StringComparison.Ordinal))
            {
                // Missing local skills must still allow old manuscripts to be viewed.
                try { book.FinalSkill = LocalSkills.Resolve(root, book.FinalSkill).Reference; }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }
            var result = AnalysisData.ReadResult(root, version);
            var work = AssetProduction.Load(root, version, result);
            if (book.AnalysisId != version.Id)
            {
                if (book.AnalysisId != null && File.Exists(Record(root)))
                {
                    var archived = AgentData.Inside(root, @"影片制作\历史\" + book.Revision + ".json");
                    Directory.CreateDirectory(Path.GetDirectoryName(archived));
                    if (!File.Exists(archived))
                    {
                        try
                        {
                            File.Copy(Record(root), archived, false);
                        }
                        catch (IOException)
                        {
                            if (!File.Exists(archived))
                                throw;
                        }
                    }
                }

                book.SyncRequired = true;
                if (String.IsNullOrWhiteSpace(book.Brief.Style))
                    book.Brief.Style = work.Style;
                var remap = new Dictionary<string, string>();
                var assets = new List<FilmAsset>();
                foreach (var item in work.Items)
                {
                    var prior = book.Assets.FirstOrDefault(x => !remap.ContainsKey(x.Id) && x.Name == item.Source.name && x.Category == item.Source.category);
                    var asset = new FilmAsset
                    {
                        Id = item.Id,
                        Name = item.Source.name,
                        Identity = item.Source.identity,
                        Category = item.Source.category,
                        Look = item.Source.variant
                    };
                    if (prior != null)
                    {
                        remap.Add(prior.Id, asset.Id);
                        asset.Look = prior.Look;
                        asset.Actor = prior.Actor;
                        asset.Images = prior.Images.ToList();
                    }

                    assets.Add(asset);
                }

                foreach (var custom in book.Assets.Where(x => x.Id.StartsWith("F", StringComparison.Ordinal) && !remap.ContainsKey(x.Id)))
                {
                    assets.Add(custom);
                    remap.Add(custom.Id, custom.Id);
                }

                var previousScenes = book.Scenes.ToList();
                var spans = result.Parts.SelectMany(part => part.scenes).OrderBy(s => s.first).Select(span =>
                {
                    var raw = source.Lines.Skip(span.first - 1).Take(span.last - span.first + 1).ToArray();
                    return new
                    {
                        span,
                        raw,
                        sig = Hash(raw)
                    };
                }).ToList();
                var matches = new FilmScene[spans.Count];
                for (int i = 0; i < spans.Count; i++)
                {
                    matches[i] = previousScenes.FirstOrDefault(s => s.SourceHash == spans[i].sig);
                    if (matches[i] != null)
                        previousScenes.Remove(matches[i]);
                }

                var scenes = new List<FilmScene>();
                for (int i = 0; i < spans.Count; i++)
                {
                    var input = spans[i];
                    var span = input.span;
                    var prior = matches[i];
                    if (prior == null)
                    {
                        prior = previousScenes.Where(other => other.Title == span.title).OrderBy(other => Math.Abs(other.First - span.first)).FirstOrDefault();
                        if (prior != null)
                            previousScenes.Remove(prior);
                    }

                    var s = new FilmScene
                    {
                        Id = prior == null ? Id() : prior.Id,
                        Title = String.IsNullOrWhiteSpace(span.episode) ? span.title : span.episode + " · " + span.title,
                        First = span.first,
                        Last = span.last,
                        SourceHash = input.sig,
                        Included = !ProductionRules.Excluded(span,input.raw) && (prior == null || prior.Included)
                    };
                    s.Cues = Cues(s.Id, input.raw, span.first);
                    s.CueFormat = 2;
                    if (prior != null && prior.SourceHash == input.sig && prior.Shots.All(sh => sh.assets.All(remap.ContainsKey)))
                    {
                        s.Shots = prior.Shots;
                        s.Segments = prior.Segments;
                        s.BoardSkill = prior.BoardSkill;
                        foreach (var sh in s.Shots)
                            sh.assets = sh.assets.Select(id => remap[id]).ToList();
                        s.Status = "已继承旧稿，需核对新资产";
                        int delta = span.first - prior.First;
                        s.Cues = prior.Cues;
                        s.CueFormat = prior.CueFormat;
                        foreach (var cue in s.Cues)
                        {
                            cue.First += delta;
                            cue.Last += delta;
                        }
                    }

                    scenes.Add(s);
                }

                book.Assets = assets;
                book.Scenes = scenes;
                book.AnalysisId = version.Id;
                book.DeliveryId = null;
            }

            if (work.DeliveredId != book.DeliveryId)
            {
                book.SyncRequired = true;
                var delivery = AssetProduction.ReadDelivery(root, work);
                foreach (var s in book.Scenes)
                {
                    s.Approved = false;
                    s.Grouped = false;
                }

                book.DeliveryId = work.DeliveredId;
                if (delivery != null)
                    foreach (var item in delivery.Items)
                    {
                        var a = book.Assets.FirstOrDefault(x => x.Id == item.Id);
                        if (a == null || item.Active == null) continue;
                        var currentMain = Image(a, "主图");
                        if (currentMain != null && String.Equals(currentMain.Digest, item.Active.Digest, StringComparison.OrdinalIgnoreCase) && currentMain.Path == item.Active.Path)
                            continue;
                        a.Images.Add(new FilmImage { Angle = "主图", Path = item.Active.Path, Digest = item.Active.Digest });
                    }
            }

            foreach (var item in work.Items)
            {
                var asset = book.Assets.FirstOrDefault(a => a.Id == item.Id);
                if (asset == null) continue;
                asset.Scope = item.Source.direction;
                asset.Evidence = item.Source.evidence;
                var active = item.Attempts == null ? null : item.Active;
                var bound = Image(asset,"主图");
                if (active != null && bound != null && active.Digest == bound.Digest)
                {
                    var panels = Regex.Matches(active.Prompt ?? "", @"(?:左格|右格|上格|下格|第[一二三四五六七八九\d]+格)(?:为|是|呈现|表现|展示|描绘|[：:])[^。\n；;]{1,160}");
                    asset.Panels = String.Join("；", panels.Cast<Match>().Select(m=>m.Value));
                }
            }
            book.Background=ProductionRules.Background(result);
            var byLine = result.Parts.SelectMany(p=>p.scenes).ToLookup(s=>s.first);
            foreach (var s in book.Scenes)
            {
                var note = DirectorPipeline.MatchScene(book.Director, s.First, s.Last, s.Title);
                if (note != null)
                {
                    s.DirectorSummary = String.Join("；", new[] { note.plotCore, note.visualFocus, note.emotionFocus, note.atmosphere, note.specialNotes }.Where(x => !String.IsNullOrWhiteSpace(x)));
                    s.EpisodeBeats = note.beats;
                    s.MustShots = note.requiredShots ?? new List<string>();
                }
                var span=byLine[s.First].FirstOrDefault(a=>a.last==s.Last);
                if(span!=null && ProductionRules.Excluded(span,source.Lines.Skip(s.First-1).Take(s.Last-s.First+1))) {
                    s.Included=false;
                    s.ProductionExcluded=true;
                }
                if (s.CueFormat < 2 && s.Shots.Count==0 && span!=null)
                {
                    var raw=source.Lines.Skip(s.First-1).Take(s.Last-s.First+1).ToArray();
                    // Preserve human corrections. Only replace an untouched legacy extraction.
                    if (Hash(s.Cues)==Hash(ParseCues(s.Id,raw,s.First,true)))
                        s.Cues=Cues(s.Id,raw,s.First);
                    s.CueFormat=2;
                    book.SyncRequired=true;
                }
                Refine(s.Cues);
            }
            return book;
        }

        internal static void Save(string root, FilmBook book)
        {
            using (new ProjectTaskLease(root, "保存影片工作台"))
            {
                var old = AgentData.Read<FilmBook>(Record(root));
                if ((old == null ? null : old.Revision) != book.Revision)
                    throw new IOException("项目已在另一窗口更新，请刷新后再保存。");
                if (book.AnalysisId != null)
                {
                    var version = AnalysisData.Load(root).Confirmed(AnalysisData.CurrentScript(root));
                    if (version == null || version.Id != book.AnalysisId)
                        throw new IOException("剧本已更换，请刷新工作台。");
                    var work = AssetProduction.Load(root, version, AnalysisData.ReadResult(root, version));
                    if (work.DeliveredId != book.DeliveryId)
                        throw new IOException("资产交接已更新，请刷新工作台。");
                }

                var revision = Id();
                if (book.AnalysisId == null && old != null && old.AnalysisId != null)
                {
                    old.Brief = book.Brief;
                    old.Revision = revision;
                    AgentData.Save(Record(root), old);
                }
                else
                {
                    var before = book.Revision;
                    book.Revision = revision;
                    try
                    {
                        AgentData.Save(Record(root), book);
                    }
                    catch
                    {
                        book.Revision = before;
                        throw;
                    }
                }

                book.Revision = revision;
                book.SyncRequired = false;
            }
        }

        private static readonly Regex Dialogue = new Regex(@"^(?<name>[\p{L}][\p{L}\p{N} .·'’\-]{0,32}?)(?:\s*[(（](?<mood>[^)）]*)[)）])?\s*(?<voice>(?:[OVS]\s*\.?\s*){2})?\s*[:：]\s*(?<text>.+)$", RegexOptions.IgnoreCase);
        private static bool EnglishWords(string text)
        {
            return Regex.IsMatch(text, @"[A-Za-z]{2,}|\b[AI]\b") &&
                !Regex.IsMatch(text.Trim(), @"^(?:O\.?S|V\.?O|BGM|SFX)\.?$",RegexOptions.IgnoreCase);
        }

        private static bool Translation(string first, string second)
        {
            return HasHan(first) != HasHan(second) && EnglishWords(HasHan(first) ? second : first);
        }

        private static Match InlineTranslation(string text)
        {
            var bracket = Regex.Match(text, @"^(.+?)\s*[(（]([^()（）]+)[)）]\s*$");
            if (bracket.Success && Translation(bracket.Groups[1].Value,bracket.Groups[2].Value) &&
                !Regex.IsMatch(bracket.Groups[2].Value,@"^(?:低声|大喊|冷笑|哭泣|愤怒|小声|耳语|停顿|叹气|画外音|旁白|whispers?|shouts?|sighs?|angrily)$",RegexOptions.IgnoreCase))
                return bracket;
            foreach (Match edge in Regex.Matches(text, @"(?<=[。！？!?…\.])[""”']?\s*|\s+(?=[A-Za-z\u3400-\u9fff])"))
            {
                int split = edge.Index + edge.Length;
                string left = text.Substring(0,split).Trim(), right = text.Substring(split).Trim();
                if (!Translation(left,right)) continue;
                string english = HasHan(left) ? right : left;
                if (!Regex.IsMatch(english, @"^[A-Za-z""'“‘]") || Regex.IsMatch(english, @"[，、；：]")) continue;
                int words = Regex.Matches(english, @"\b[A-Za-z]+(?:['’][A-Za-z]+)?\b").Count;
                if (words < 2 && (!Regex.IsMatch(english,@"[.!?][""”']?$") ||
                    !Regex.IsMatch(left,@"[。！？!?…\.][""”']?$") && !Regex.IsMatch(english,@"^(?:Run|Stop|No|Yes|Go|Wait|Help)\W+$",RegexOptions.IgnoreCase))) continue;
                if (!Regex.IsMatch(left, @"[。！？!?…\.][""”']?$") && !Regex.IsMatch(english,@"[.!?][""”']?$")) continue;
                return Regex.Match(text, @"^(.{" + split + @"})(.+)$");
            }
            return Match.Empty;
        }

        internal static List<FilmCue> Cues(string sceneId, string[] lines, int start)
        {
            return ParseCues(sceneId,lines,start,false);
        }

        private static List<FilmCue> ParseCues(string sceneId, string[] lines, int start, bool legacy)
        {
            var cues = new List<FilmCue>();
            FilmCue pairedEnd = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = Clean(lines[i]);
                if (line.Length == 0)
                    continue;
                var match = Dialogue.Match(line);
                var prior = cues.LastOrDefault();
                bool header = Regex.IsMatch(line, @"^(?:[【〖△(（]|\d+\s*[-－彐]|第.{1,5}集|(?:人物|场景|地点|字幕|片名|备选剧名|题材方向|总集数|一句话|故事大纲|时间|镜头)[:：])");
                if (!match.Success && !header && prior != null && prior.Type == "对白" && (legacy || !Translation(prior.Text,line)) && !Regex.IsMatch(prior.Text, @"[。！？!?…\.][”'""]?$"))
                {
                    prior.Text += (Regex.IsMatch(line, @"^[A-Za-z]") ? " " : "") + line;
                    prior.Last = start + i;
                    continue;
                }

                var cue = new FilmCue
                {
                    First = start + i,
                    Last = start + i,
                    Text = line,
                    Type = "画面",
                    Id = sceneId + "-" + (i + 1)
                };
                if (!header && match.Success)
                {
                    cue.Type = "对白";
                    cue.Speaker = match.Groups["name"].Value.Trim();
                    cue.Voice = (match.Groups["mood"].Value + " " + match.Groups["voice"].Value).Trim();
                    cue.Text = match.Groups["text"].Value;
                }

                if (!legacy && !header && !match.Success && prior != null && prior != pairedEnd && prior.Type == "对白" &&
                    prior.Last+1 == cue.First && Translation(prior.Text,line))
                {
                    cue.Type = "对白"; cue.Speaker = prior.Speaker; cue.Voice = prior.Voice;
                }

                if (cue.Type == "对白")
                {
                    var tag = Regex.Match(cue.Speaker, @"^(?:中文(?:翻译|译文)?|中译|译文|英文(?:原文)?|English|Chinese|EN|ZH)$", RegexOptions.IgnoreCase);
                    if (tag.Success && prior != null && prior.Type == "对白") cue.Speaker = prior.Speaker;
                    var inline = Regex.Match(cue.Text, @"^(.+?)\s*[(（]\s*(?:中文(?:翻译|译文)?|中译|译文|英文(?:原文)?|English|Chinese|EN|ZH)\s*[:：]\s*(.+?)\s*[)）]\s*$",RegexOptions.IgnoreCase);
                    if (!legacy && !inline.Success) inline = InlineTranslation(cue.Text);
                    if (inline.Success && HasHan(inline.Groups[1].Value) != HasHan(inline.Groups[2].Value))
                    {
                        cue.Text = inline.Groups[1].Value.Trim();
                        cues.Add(cue);
                        cue = new FilmCue { Id=cue.Id+"-alt", Type="对白", First=cue.First, Last=cue.Last,
                            Speaker=cue.Speaker, Voice=cue.Voice, Text=inline.Groups[2].Value.Trim() };
                        if (!legacy) {
                            cues[cues.Count-1].Language = HasHan(cues[cues.Count-1].Text) ? "中文" : "英文";
                            cue.Language = HasHan(cue.Text) ? "中文" : "英文";
                        }
                        pairedEnd = cue;
                    }
                }
                if (prior != null && prior != pairedEnd && prior.Type == "对白" && cue.Type == "对白" &&
                    (String.Equals(prior.Speaker,cue.Speaker,StringComparison.OrdinalIgnoreCase) || HasHan(prior.Speaker) != HasHan(cue.Speaker)) &&
                    Translation(prior.Text,cue.Text)) pairedEnd = cue;
                cues.Add(cue);
            }

            Refine(cues);
            for (int i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                if (c.Type != "对白" || c.Language != null)
                    continue;
                var other = i + 1 < cues.Count ? cues[i + 1] : null;
                if (other == null || other.Type != "对白" || other.Language != null || other.First < c.Last) continue;
                bool same = String.Equals(c.Speaker, other.Speaker, StringComparison.OrdinalIgnoreCase);
                bool localized = HasHan(c.Speaker) != HasHan(other.Speaker);
                if (!same && !localized) continue;
                if (HasHan(c.Text) != HasHan(other.Text) && (same ||
                    (HasHan(c.Text) == HasHan(c.Speaker) && HasHan(other.Text) == HasHan(other.Speaker))))
                {
                    c.Language = HasHan(c.Text) ? "中文" : "英文";
                    other.Language = HasHan(c.Text) ? "英文" : "中文";
                    i++;
                }
                else if (same && c.Text == other.Text && !HasHan(c.Text) && cues.Any(x=>x.Language == "中文"))
                {
                    c.Language = "中文"; other.Language = "英文"; i++;
                }
            }
            return cues;
        }

        internal static void Refine(List<FilmCue> cues)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                if (c.Type != "对白")
                    continue;
                var speaker = Regex.Match(c.Speaker ?? "", @"^(?<speaker>[\p{L}·]+)\s*(?<action>(?:对视|望向|看向|看着|转向|望着).+)$");
                if (speaker.Success)
                {
                    c.Speaker = speaker.Groups["speaker"].Value;
                    c.Note = (c.Note + " " + speaker.Groups["action"].Value).Trim();
                }

                if (HasHan(c.Text) && (c.Text.Contains("(") || c.Text.Contains("（")))
                {
                    c.Text = Regex.Replace(c.Text, @"[(（]([^()（）]{1,75})[)）]", m =>
                    {
                        c.Note = (c.Note + " " + m.Groups[1].Value).Trim();
                        return "";
                    }).Trim();
                }
            }
        }

        private static bool HasHan(string value)
        {
            return Regex.IsMatch(value ?? "", @"[\u3400-\u9fff]");
        }

        internal static string LanguageRule(string language)
        {
            return language == "中文" || language == "英文" ? "本剧只使用" + language +
                "原句作为对白和画外音。译文与原句属于同一次表演，勿补译、改写或指示使用另一种配音。保留动作和说话方式。成片省略所有字幕和字卡。" : "";
        }

        internal static bool PairBoundary(IList<FilmCue> cues, int next)
        {
            if (next <= 0 || next >= cues.Count) return false;
            // Consume pairs in reading order. Adjacent exchanges may belong to the same speaker.
            for (int i = 0; i < cues.Count - 1 && i < next; i++)
            {
                var a = cues[i]; var b = cues[i+1];
                if (a.Type != "对白" || b.Type != "对白" || a.Language == null || b.Language == null ||
                    a.Language == b.Language || b.First < a.Last) continue;
                if (i+1 == next) return true;
                i++;
            }
            return false;
        }

        internal static string[] ProductionLines(string[] source, string language)
        {
            if (language != "英文" && language != "中文") return source;
            var lines = (string[])source.Clone();
            var cues = Cues("source", source, 1);
            var sameLine = cues.Where(c=>c.Type == "对白" && c.Language == language).ToDictionary(c=>c.First);
            foreach (var cue in cues.Where(c => c.Language != null && c.Language != language))
            {
                string note = String.Join(" ", new[]{cue.Voice, cue.Note}.Where(x=>!String.IsNullOrWhiteSpace(x)));
                for (int i = cue.First; i <= cue.Last; i++)
                {
                    FilmCue kept;
                    if (sameLine.TryGetValue(i,out kept)) lines[i-1] = FilmWriter.SourceText(kept);
                    else lines[i-1] = i == cue.First && note.Length > 0 ? "同句表演提示：" + note : "（同句译文已省略）";
                }
            }
            return lines;
        }

        internal static bool Spoken(FilmCue cue, string language)
        {
            if (cue.Type != "对白")
                return false;
            if (language == "原文")
                return true;
            if (cue.Language != null)
                return cue.Language == language;
            return true;
        }

        internal static string VoiceConflict(string language, string direction)
        {
            if (language != "英文" && language != "中文") return null;
            string other = language == "英文" ? "(?:中文|汉语|普通话)" : "(?:英文|英语)";
            return Regex.IsMatch(direction ?? "", other + @"(?:的)?\s*(?:画外音|对白|旁白|配音|台词|独白|说出|念出|喊出)|(?:用|以|使用)" + other + @"(?:来)?(?:说|喊|念|读|配音|发声)") ?
                "镜头的配音指令与项目对白语言「" + language + "」冲突。" : null;
        }

        internal static FilmImage Image(FilmAsset asset, string angle)
        {
            var main = asset.Images.LastOrDefault(i => i.Angle == "主图");
            return asset.Images.LastOrDefault(i => i.Angle == angle && i.Parent != null && main != null && i.Parent == main.Digest) ?? main;
        }

        internal static void Import(string root, FilmAsset asset, string angle, string file)
        {
            if (!Angles.Contains(angle))
                throw new InvalidDataException("机位无效。");
            var main = Image(asset, "主图");
            if (angle != "主图" && main == null)
                throw new InvalidDataException("请先选择主图。");
            var probe = new AssetAttempt();
            AssetProduction.Inspect(file, probe);
            string ext = probe.Format == "JPEG" ? ".jpg" : "." + probe.Format.ToLowerInvariant();
            var relative = Path.Combine("影片制作", "参考图", probe.Digest + ext);
            var dest = AgentData.Inside(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (!File.Exists(dest))
                File.Copy(file, dest, false);
            if (SystemData.Digest(dest) != probe.Digest)
                throw new InvalidDataException("参考图副本校验失败。");
            asset.Images.Add(new FilmImage { Angle = angle, Path = relative, Digest = probe.Digest, Parent = angle == "主图" ? null : main.Digest });
        }

        internal static IEnumerable<FilmCue> Covered(FilmScene s, FilmShot shot)
        {
            int first = shot.first + (shot.continuation ? 1 : 0);
            return s.Cues.Skip(first - 1).Take(shot.last - first + 1);
        }

        internal static void CheckShots(FilmBook b, FilmScene s, IList<FilmShot> shots)
        {
            int next = 1;
            for (int i = 0; i < shots.Count; i++)
            {
                var shot = shots[i];
                string row = "第 " + (i+1) + " 镜 ";
                if (shot == null) throw new InvalidDataException(row + "为空。");
                int expected = shot.continuation ? next-1 : next;
                if (shot.first != expected || (shot.continuation && i == 0) || shot.last < shot.first || shot.last > s.Cues.Count)
                    throw new InvalidDataException(row + "first/last 为 " + shot.first + "/" + shot.last + "，期望从单元 " + expected + " 接续，末单元不超过 " + s.Cues.Count);
                if (shot.seconds < 1 || shot.seconds > 30) throw new InvalidDataException(row + "seconds 需为 1–30。");
                if (String.IsNullOrWhiteSpace(shot.action)) throw new InvalidDataException(row + "action 为空。");
                if (String.IsNullOrWhiteSpace(shot.camera)) throw new InvalidDataException(row + "camera 为空。");
                if (String.IsNullOrWhiteSpace(shot.end)) throw new InvalidDataException(row + "end 为空。");
                if (!Angles.Contains(shot.angle)) throw new InvalidDataException(row + "angle 无效：" + shot.angle);
                if (shot.assets == null) throw new InvalidDataException(row + "assets 缺失。");
                foreach (string id in shot.assets)
                    if (!b.Assets.Any(a => a.Id == id)) throw new InvalidDataException(row + "assets 无效 Id：" + id);
                if (shot.continuation && !String.IsNullOrWhiteSpace(shot.dialogueSlice))
                {
                    int start = shot.first - 1;
                    if (PairBoundary(s.Cues, start)) start--;
                    var lines = s.Cues.Skip(start).Take(shot.first - start).Where(c => Spoken(c,b.Brief.Language))
                        .Select(c => c.Text).Where(line => !String.IsNullOrWhiteSpace(line) && shot.dialogueSlice.Contains(line));
                    foreach (string line in lines)
                        for (int j = i - 1; j >= 0 && shots[j].last == shot.first; j--)
                            if ((shots[j].dialogueSlice ?? "").Contains(line))
                                throw new InvalidDataException("第 " + (i + 1) + " 镜重复整句续接对白。");
                }
                var conflict = VoiceConflict(b.Brief.Language, String.Join(" ", shot.action, shot.end, shot.camera, shot.content, shot.dialogueSlice));
                if (conflict != null) throw new InvalidDataException(conflict);
                next = shot.last + 1;
            }

            if (next != s.Cues.Count + 1 || shots.Count == 0)
                throw new InvalidDataException("尚有未覆盖的原文单元 " + next + "。");
        }

        internal static void RequireReady(string root, FilmBook b, FilmScene scene)
        {
            if (b.DeliveryId == null)
                throw new InvalidOperationException("请在资产页确认交接本版资产图。");
            CheckShots(b, scene, scene.Shots);
            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shot in scene.Shots)
                foreach (var id in shot.assets.Distinct())
                {
                    var asset = b.Assets.First(a => a.Id == id);
                    var image = Image(asset, shot.angle);
                    if (image == null)
                        throw new InvalidDataException(asset.Name + " 缺少参考图。");
                    var file = AgentData.Inside(root, image.Path);
                    if (verified.Add(file + "|" + image.Digest) && (!File.Exists(file) || SystemData.Digest(file) != image.Digest))
                        throw new InvalidDataException(asset.Name + " 缺少可核对的参考图。");
                }
        }

        internal static void GroupProject(string root, FilmBook book)
        {
            var scenes = book.Scenes.Where(s => s.Included).ToArray();
            if (scenes.Length == 0) throw new InvalidDataException("请先生成分镜表。");
            foreach (var scene in scenes)
            {
                try { RequireReady(root, book, scene); }
                catch (Exception e) { throw new InvalidDataException(scene.Title + " · " + e.Message, e); }
            }
            foreach (var scene in scenes)
            {
                scene.Approved = true;
                MatchGroups(scene,30,book,VideoPlatform.ProjectModel(root,book.Brief.Ratio));
            }
            Save(root, book);
        }

        internal static bool ContinuationBoundary(FilmScene s, int shotIndex)
        {
            return shotIndex >= 0 && shotIndex + 1 < s.Shots.Count &&
                (s.Shots[shotIndex + 1].continuation || s.Shots[shotIndex + 1].first == s.Shots[shotIndex].last);
        }

        internal static bool ProtectedBoundary(FilmScene s, int shotIndex)
        {
            return shotIndex >= 0 && shotIndex + 1 < s.Shots.Count &&
                (ContinuationBoundary(s, shotIndex) || PairBoundary(s.Cues, s.Shots[shotIndex].last));
        }

        internal static List<FilmSegment> Group(FilmScene s, int limit)
        {
            if (limit < 1 || limit > 30)
                throw new InvalidDataException("单段时长范围为 1–30 秒。");
            var result = new List<FilmSegment>();
            int total = 0;
            for (int i = 0; i < s.Shots.Count; i++)
            {
                int first = i;
                while (ProtectedBoundary(s, i)) i++;
                int seconds = s.Shots.Skip(first).Take(i-first+1).Sum(sh=>sh.seconds);
                if (seconds > limit)
                    throw new InvalidDataException("第 " + (first + 1) + "–" + (i+1) + " 镜的连续对白超过分段上限，请先调整镜头时长。");
                if (first == 0 || total + seconds > limit)
                {
                    result.Add(new FilmSegment { First = first, Last = i });
                    total = 0;
                }
                result.Last().Last = i;
                total += seconds;
            }

            return result;
        }

        internal static void MatchGroups(FilmScene s, int limit,FilmBook book=null,VideoModel target=null)
        {
            if (target == null && book != null && book.V2Builtin) target = new VideoModel { Min=1, Max=30, Images=9 };
            var groups = Group(s, target==null?limit:Math.Min(target.Max,limit));
            if(target!=null)groups=VideoDelivery.FitToLimits(book,s,groups,target,Math.Min(30,Math.Min(limit,target.Max)));
            s.Segments = groups.Select(g => s.Segments.FirstOrDefault(old => old.First == g.First && old.Last == g.Last) ?? g).ToList();
            s.Grouped = true;
        }

        internal static IEnumerable<FilmShot> Shots(FilmScene s, FilmSegment seg)
        {
            return s.Shots.Skip(seg.First).Take(seg.Last - seg.First + 1);
        }

        internal static void Pickup(FilmBook b, FilmScene s, FilmCue cue, string action, string camera, int seconds)
        {
            int n = s.Cues.IndexOf(cue) + 1;
            if (cue.Type != "对白" || n < 1)
                throw new InvalidOperationException("请选择原文对白。");
            var result = new List<FilmShot>();
            bool placed = false;
            foreach (var shot in s.Shots)
            {
                if (n < shot.first && !placed)
                {
                    result.Add(PickupShot(n, action, camera, seconds, shot));
                    placed = true;
                }

                if (n < shot.first || n > shot.last)
                {
                    result.Add(shot);
                    continue;
                }

                if (n > shot.first)
                    result.Add(Part(shot, shot.first, n - 1));
                result.Add(PickupShot(n, action, camera, seconds, shot));
                placed = true;
                if (n < shot.last)
                    result.Add(Part(shot, n + 1, shot.last));
            }

            if (!placed)
                result.Add(PickupShot(n, action, camera, seconds, s.Shots.LastOrDefault()));
            CheckShots(b, s, result);
            s.Shots = result;
            s.Approved = s.Grouped = false;
        }

        private static FilmShot PickupShot(int n, string action, string camera, int seconds, FilmShot basis)
        {
            return new FilmShot
            {
                first = n,
                last = n,
                seconds = seconds,
                action = action,
                camera = camera,
                end = basis == null ? "回到本场连续状态" : basis.end,
                angle = basis == null ? "主图" : basis.angle,
                assets = basis == null ? new List<string>() : basis.assets.ToList()
            };
        }

        private static FilmShot Part(FilmShot s, int first, int last)
        {
            return new FilmShot
            {
                first = first,
                last = last,
                seconds = Math.Max(1, (int)Math.Round(s.seconds * (last - first + 1.0) / (s.last - s.first + 1))),
                action = s.action,
                camera = s.camera,
                end = s.end,
                angle = s.angle,
                assets = s.assets.ToList()
            };
        }

        internal static void Changed(FilmBook b, string asset)
        {
            foreach (var s in b.Scenes.Where(s => asset == null || s.Shots.Any(sh => sh.assets.Contains(asset))))
            {
                s.Approved = false;
                s.Grouped = false;
            }
        }
    }
}
