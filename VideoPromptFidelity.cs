using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    internal static class VideoPromptFidelity
    {
        private static readonly Regex Placeholder = new Regex(@"[【\[]\s*此处\s*[@＠]\s*(?<name>[^】\]\r\n]+?)\s*[】\]]");
        private static readonly Regex ImageNumber = new Regex(@"@Image\d+", RegexOptions.IgnoreCase);
        private static readonly Regex GlobalReferences = new Regex(@"^(?<prefix>全局(?:场景|人物|角色|道具)参考[：:][ \t]*)(?<body>[^\r\n]*)", RegexOptions.Multiline);
        private static readonly Regex ReferenceItem = new Regex(@"^(?<subject>.+?)参考[ \t]*$");

        private static string GlobalItems(string text, Func<string,string> resolve)
        {
            return GlobalReferences.Replace(text, line => line.Groups["prefix"].Value +
                Regex.Replace(line.Groups["body"].Value, @"[^；;]+", item => {
                    string word = item.Value.Trim();
                    if (word == "无" || word.Length == 0 || Regex.IsMatch(word,@"^@Image\d+$")) return item.Value;
                    var match = ReferenceItem.Match(item.Value);
                    string subject = match.Success ? match.Groups["subject"].Value.Trim() : word;
                    return resolve(subject) ?? item.Value;
                }));
        }
        private static readonly Regex Range = new Regex(@"(?<![\d.])(\d+(?:\.\d+)?)\s*(?:秒|s)?\s*[-–—~～至]\s*(\d+(?:\.\d+)?)\s*(?:秒|s\b)", RegexOptions.IgnoreCase);

        internal static bool IsFaithful(FilmPrompt prompt)
        {
            return prompt != null && prompt.SkillFaithful;
        }

        internal static string DisplayText(FilmPrompt prompt, IList<PromptReference> references)
        {
            if (prompt == null) return "";
            return IsFaithful(prompt) ? prompt.Text ?? "" : ProductionRules.Body(prompt.Text, references);
        }

        private static bool Auxiliary(string subject)
        {
            return Regex.IsMatch(subject, @"音色|音频|声线|声音参考|站位|位置关系");
        }

        private static string Identity(PromptReference reference)
        {
            if (!String.IsNullOrWhiteSpace(reference.Identity)) return reference.Identity.Trim();
            return (reference.Name ?? "").Split(new[] { " · " }, StringSplitOptions.None)[0].Trim();
        }

        private static string ReferenceWords(string value)
        {
            return Regex.Replace(value ?? "", @"[\s·（）()_-]", "");
        }

        private static string BindByKey(string name, IList<PromptReference> references, Func<PromptReference,string> key)
        {
            var matches = references.Where(r => !String.IsNullOrWhiteSpace(key(r)) && name.StartsWith(key(r), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => key(r).Length).ToList();
            if (matches.Count == 0) return null;
            int length = key(matches[0]).Length;
            string tail = ReferenceWords(name.Substring(length));
            var candidates = new List<PromptReference>();
            int best = -1;
            foreach (var r in matches.Where(r => key(r).Length == length))
            {
                string rest = tail;
                foreach (string word in new[] { ReferenceWords(r.Look), ReferenceWords(r.Angle) }.Where(w => w.Length > 0).OrderByDescending(w => w.Length))
                    rest = rest.Replace(word, "");
                if (!Regex.IsMatch(rest, @"^(?:的|形象|人物|角色|场景|道具|服装|造型|头部|面部|全身|身份|三视|四视|设定|参考|素材|主图|图片|照|图)*$")) continue;
                int specificity = tail.Length - rest.Length;
                if (specificity < best) continue;
                if (specificity > best) { candidates.Clear(); best = specificity; }
                candidates.Add(r);
            }
            var labels = candidates.Select(r => r.Label).Distinct().ToList();
            if (labels.Count > 1) throw new InvalidDataException("素材有多个可用机位或造型，请标明具体引用：" + name);
            return labels.FirstOrDefault();
        }

        private static string Bind(Match marker, IList<PromptReference> references)
        {
            string name = marker.Groups["name"].Value.Trim();
            var explicitLabel = references.FirstOrDefault(r => String.Equals(r.Label, "@" + name, StringComparison.Ordinal));
            if (explicitLabel != null) return explicitLabel.Label;
            if (Auxiliary(name) && !references.Any(r => r.Name == name)) return marker.Value;
            return ResolveSubject(name,references) ?? marker.Value;
        }

        internal static string BindForSubmission(FilmPrompt prompt, IList<PromptReference> references)
        {
            references = references ?? new List<PromptReference>();
            if (!IsFaithful(prompt)) return DisplayText(prompt, references);
            string text = Placeholder.Replace(prompt.Text ?? "", m => Bind(m, references));
            return GlobalItems(text, name => {
                string label = ResolveSubject(name,references);
                if (label == null) throw new InvalidDataException("本段未绑定参考素材：" + name);
                return label;
            });
        }

        private static string ResolveSubject(string name, IList<PromptReference> references)
        {
            return BindByKey(name,references,r => r.Name) ?? BindByKey(name,references,Identity);
        }

        private static bool HasAction(string block, IList<PromptReference> references)
        {
            foreach (string line in block.Split('\n'))
            {
                string rest = line.Trim();
                if (Regex.IsMatch(rest,@"^(?:视觉风格|全局(?:场景|人物|角色|道具|光影)参考)[：:]|^无配乐无字幕$|^全局禁止出现任何字幕")) continue;
                if (Regex.IsMatch(rest, @"^[|｜\s]*[^。！？!?\r\n]*[—]{2,}\s*$")) continue;
                if (Regex.IsMatch(rest, @"^(?:#{1,6}\s+|【\s*\d+[.．、])")) break;
                if (Regex.IsMatch(rest, @"^(?:参考图|素材|引用|资产|配音|音色|台词|表演)[：:]") &&
                    !rest.StartsWith("表演", StringComparison.Ordinal)) continue;
                rest = Placeholder.Replace(rest, "");
                rest = ImageNumber.Replace(rest, "");
                foreach (var r in references.OrderByDescending(r => (r.Caption ?? "").Length))
                    foreach (var value in new[] { r.Name, r.Angle, r.Look, r.Actor })
                        if (!String.IsNullOrWhiteSpace(value)) rest = rest.Replace(value, "");
                rest = Regex.Replace(rest, @"(?:第\s*\d+\s*镜|镜头\s*\d+|SHOT\s*\d+|\d+-\d+[A-Z]+|参考图|素材|主图|参考)", "", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(rest, @"[\p{L}]")) return true;
            }
            return false;
        }

        private static void CheckTimeline(string text, IList<PromptReference> references, List<string> issues)
        {
            if (Regex.IsMatch(text,@"\A视觉风格[：:]"))
            {
                var firstShot = Regex.Match(text,@"(?m)^[ \t]*\d+\s*[-–—~～至]\s*\d+\s*秒。");
                if (firstShot.Success) text = text.Substring(firstShot.Index);
            }
            var ranges = Range.Matches(text).Cast<Match>().ToList();
            // A skill can contain both an overview and the expanded timeline.
            var expanded = ranges.GroupBy(m => m.Groups[1].Value + "/" + m.Groups[2].Value)
                .Select(g => g.Last()).ToList();
            foreach (var m in expanded)
            {
                int start = m.Index + m.Length;
                var next = m.NextMatch();
                if (next.Success)
                {
                    var culture = System.Globalization.CultureInfo.InvariantCulture;
                    double from = Double.Parse(m.Groups[1].Value, culture), to = Double.Parse(m.Groups[2].Value, culture);
                    double nextFrom = Double.Parse(next.Groups[1].Value, culture), nextTo = Double.Parse(next.Groups[2].Value, culture);
                    if (nextFrom >= from && nextTo <= to && (nextFrom > from || nextTo < to)) continue;
                }
                string block = text.Substring(start, (next.Success ? next.Index : text.Length) - start);
                if (!HasAction(block, references))
                    issues.Add(m.Value + " 缺少镜头动作正文。");
            }
        }

        private static string Core(string value)
        {
            string normalized = (value ?? "").Replace('，',',').Replace('？','?').Replace('！','!').Replace('：',':')
                .Replace('；',';').Replace('（','(').Replace('）',')').Replace('＂','"')
                .Replace('“','"').Replace('”','"').Replace('「','"').Replace('」','"')
                .Replace('’',(char)39).Replace('‘',(char)39).Replace('。','.').Replace('、',',');
            return Regex.Replace(normalized.Trim(), @"\s+", " ");
        }

        private static bool ContainsTranslation(string body, string quote)
        {
            if (quote.Length > 4) return Core(body).Contains(quote);
            // A short word can occur in an unrelated shot description. Require a
            // dialogue boundary before treating it as an inserted translation.
            return Regex.IsMatch(Core(body), @"(?:^|[“「""：:])\s*" + Regex.Escape(quote) + @"\s*(?:$|[”」""])",
                RegexOptions.Multiline);
        }

        private static bool IsAsciiWord(char c)
        {
            return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == (char)39;
        }

        private static bool WordBoundary(string text, int index)
        {
            return index <= 0 || index >= text.Length || !(IsAsciiWord(text[index - 1]) && IsAsciiWord(text[index]));
        }

        private static int FindAtBoundary(string body, string part, int cursor, bool requireStart, bool requireEnd)
        {
            for (int pos = body.IndexOf(part, cursor, StringComparison.Ordinal); pos >= 0;
                pos = body.IndexOf(part, pos + 1, StringComparison.Ordinal))
            {
                if ((!requireStart || WordBoundary(body, pos)) && (!requireEnd || WordBoundary(body, pos + part.Length)))
                    return pos;
            }
            return -1;
        }

        private static bool MatchFollowingSlices(string body, string quote, List<string> pieces, int next, int cursor, int offset)
        {
            for (int end = next; end < pieces.Count && offset < quote.Length; end++)
            {
                if (quote[offset] == ' ') offset++;
                int remain = quote.Length - offset;
                string piece = pieces[end];
                if (remain == 0 || piece.Length == 0) return false;
                if (piece.Length > remain)
                {
                    // A saved shot can continue into the next source line.
                    return String.CompareOrdinal(quote, offset, piece, 0, remain) == 0 && WordBoundary(piece, remain)
                        && FindAtBoundary(body, quote.Substring(offset), cursor, false, true) >= 0;
                }
                if (String.CompareOrdinal(quote, offset, piece, 0, piece.Length) != 0) return false;
                bool endsQuote = offset + piece.Length == quote.Length;
                int pos = FindAtBoundary(body, piece, cursor, false, endsQuote);
                if (pos < 0) return false;
                cursor = pos + piece.Length;
                offset += piece.Length;
                if (endsQuote) return true;
            }
            return false;
        }

        private static bool HasSlices(string body, string quote, IEnumerable<FilmShot> shots)
        {
            var pieces = new List<string>();
            foreach (var sh in shots)
            {
                string slice = Core(sh.dialogueSlice);
                if (slice.Length == 0) continue;
                var cited = Regex.Matches(slice, "[“「\\\"]([^”」\\\"]+)[”」\\\"]");
                if (cited.Count == 0) pieces.Add(slice);
                else pieces.AddRange(cited.Cast<Match>().Select(m => Core(m.Groups[1].Value)));
            }
            for (int start = 0; start + 1 < pieces.Count; start++)
            {
                string first = pieces[start];
                if (first.Length == 0) continue;
                if (quote.StartsWith(first, StringComparison.Ordinal))
                {
                    int pos = FindAtBoundary(body, first, 0, true, WordBoundary(quote, first.Length));
                    if (pos >= 0 && MatchFollowingSlices(body, quote, pieces, start + 1, pos + first.Length, first.Length))
                        return true;
                    continue;
                }

                // The first covering shot may start on the previous source line.
                for (int cut = 1; cut < first.Length; cut++)
                {
                    if (!WordBoundary(first, cut)) continue;
                    string head = first.Substring(cut).TrimStart();
                    if (head.Length == 0 || head.Length >= quote.Length || !WordBoundary(quote, head.Length)
                        || !quote.StartsWith(head, StringComparison.Ordinal)) continue;
                    int pos = FindAtBoundary(body, head, 0, true, false);
                    if (pos >= 0 && MatchFollowingSlices(body, quote, pieces, start + 1, pos + head.Length, head.Length))
                        return true;
                }
            }
            return false;
        }

        internal static string[] Validate(FilmBook book, FilmScene scene, FilmSegment segment, string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return new[] { "片段提示词正文为空。" };
            var issues = new List<string>();
            var refs = FilmWriter.References(book, scene, segment);
            var shots = Film.Shots(scene, segment).ToList();
            if (shots.Count > 1) CheckTimeline(text, refs, issues);
            if (Film.ProtectedBoundary(scene, segment.First - 1) || Film.ProtectedBoundary(scene, segment.Last))
                issues.Add("片段边界拆开了双语对白或跨镜续接。");
            var cues = shots.SelectMany(sh => Film.Covered(scene, sh)).Distinct().ToList();
            string body = Core(text);
            var spoken = cues.Where(c => Film.Spoken(c, book.Brief.Language)).ToList();
            foreach (var cue in spoken)
            {
                int n = scene.Cues.IndexOf(cue) + 1;
                string quote = Core(cue.Text);
                int pairEnd = Film.PairBoundary(scene.Cues,n) ? n + 1 : n;
                if (FindAtBoundary(body, quote, 0, true, true) < 0 && !HasSlices(body, quote, shots.Where(sh => sh.first <= pairEnd && sh.last >= n)))
                    issues.Add("原文第 " + cue.First + " 行缺少对白：" + FilmWriter.SourceText(cue));
            }
            foreach (var group in spoken.GroupBy(c => Core(c.Text)).Where(g => g.Count() > 1))
                if (Regex.Matches(body, Regex.Escape(group.Key)).Count < group.Count())
                    issues.Add("原文中有 " + group.Count() + " 次对白，正文未完整交代：" + group.Key);
            foreach (var cue in cues.Except(spoken).Where(c => c.Type == "对白" && !String.IsNullOrWhiteSpace(c.Text)))
                if (ContainsTranslation(text,Core(cue.Text)) && !spoken.Any(c => Core(c.Text).Contains(Core(cue.Text))))
                    issues.Add("混入另一语种译文：" + cue.Text);
            string conflict = Film.VoiceConflict(book.Brief.Language, text);
            if (conflict != null) issues.Add(conflict);
            if (book.Brief.Language == "原文" && cues.Any(c => c.Language == "中文") && cues.Any(c => c.Language == "英文") &&
                (Film.VoiceConflict("英文", text) != null || Film.VoiceConflict("中文", text) != null))
                issues.Add("双语原稿的配音语言冲突。");
            foreach (Match m in ImageNumber.Matches(text))
                if (!refs.Any(r => r.Label == m.Value)) issues.Add("未绑定素材 " + m.Value);
            GlobalItems(text, subject => {
                string projectName = book.Assets.Select(a => a.Name).Where(n => !String.IsNullOrWhiteSpace(n) &&
                    subject.StartsWith(n,StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Length).FirstOrDefault();
                try
                {
                    if ((projectName != null && !refs.Any(r => r.Name == projectName)) ||
                        ResolveSubject(subject,refs) == null)
                        issues.Add("本段未绑定参考素材：" + subject);
                }
                catch (InvalidDataException ex) { issues.Add(ex.Message); }
                return null;
            });
            foreach (Match m in Placeholder.Matches(text))
            {
                string subject = m.Groups["name"].Value;
                if (Auxiliary(subject)) continue;
                try
                {
                    string projectName = book.Assets.Select(a => a.Name).Where(n => !String.IsNullOrWhiteSpace(n) &&
                        subject.StartsWith(n,StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Length).FirstOrDefault();
                    if (projectName != null && !refs.Any(r => r.Name == projectName) || Bind(m, refs) == m.Value)
                        issues.Add("本段未绑定参考素材：" + subject);
                }
                catch (InvalidDataException ex) { issues.Add(ex.Message); }
            }
            return issues.Distinct().ToArray();
        }
    }
}
