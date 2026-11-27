using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OneClickFilm
{
    internal static class ProductionRules
    {
        private static readonly Regex EpisodeHeading = new Regex(@"^\s*[#【\[]*\s*(?<ep>第\s*[零〇一二两三四五六七八九十百\d]+\s*集|(?:episode|ep\.?)\s*[-:#：]?\s*\d+)(?=$|[\s:：·【】\]\-—]|[（(、.。]|集头|开场)", RegexOptions.IgnoreCase);
        internal static string Episode(string line)
        {
            return EpisodeHeading.Match(Film.Clean(line)).Groups["ep"].Value;
        }

        internal static string Background(AnalysisResult result)
        {
            return AgentData.Short(String.Join("\n", result.Parts.SelectMany(p=>p.scenes)
                .Where(s=>s.kind=="background").Select(s=>s.title+" · "+s.action).Distinct()),6000);
        }

        internal static bool Excluded(AnalysisScene scene, IEnumerable<string> raw)
        {
            if (scene.kind == "background") return true;
            if (scene.kind == "story" || scene.kind == "vo_transition") return false;
            if (scene.kind == null && Regex.IsMatch(scene.title ?? "", @"^(?:卷首资料|全剧大纲|项目资料|故事大纲|创作说明|角色小传|剧本背景资料)")) return true;
            if (scene.kind != "silent_transition" && !Regex.IsMatch(scene.title ?? "", @"^(?:黑屏|黑场|纯黑|黑底|纯色|白屏|白场)(?:$|[\s/·：]|转场|过渡|时间字卡)")) return false;
            return !raw.Any(line=>Regex.IsMatch(line,@"(?:画外音|画外声|旁白|配音|narrator|voice.over|(?<![A-Za-z])(?:v\s*\.?\s*o|o\s*\.?\s*[vs])\.?(?![A-Za-z]))",RegexOptions.IgnoreCase)
                && !Regex.IsMatch(line,@"(?:无|没有|不含|无需)(?:画外音|画外声|旁白|配音)"));
        }

        internal static string Prose(string text, IList<PromptReference> refs)
        {
            string body = Regex.Replace(text ?? "", @"[【\[]\s*此处\s*[@＠]\s*(?<asset>[^\]】\n]+)[】\]]", m => {
                string asset = m.Groups["asset"].Value;
                var hit = Regex.Match(asset, @"@Image\d+");
                var labels = refs.Where(r => !String.IsNullOrWhiteSpace(r.Name) && asset.Contains(r.Name)).OrderByDescending(r=>r.Name.Length).ToList();
                if (hit.Success && refs.Any(r=>r.Label==hit.Value)) return hit.Value;
                if(labels.Count>0) {
                    var exact=labels.Where(r=>r.Name.Length==labels[0].Name.Length).ToList();
                    var angles=exact.Where(r=>!String.IsNullOrWhiteSpace(r.Angle) && asset.Contains(r.Angle)).ToList();
                    var choices=(angles.Count>0 ? angles : exact).Select(r=>r.Label).Distinct().ToList();
                    if(choices.Count==1) return choices[0];
                }
                throw new System.IO.InvalidDataException("提示词引用未匹配资产：" + asset);
            });
            body = Regex.Replace(body,@"(@Image\d+)(?:\s+\1)+(?!\d)", "$1");
            return body;
        }

        internal static string Body(string text, IList<PromptReference> refs)
        {
            var output = new List<string>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                // A literal utterance may itself mention subtitles or a shot.
                if (Regex.IsMatch(line, @"^[^：:\n]{1,80}[：:][“""].*[”""]$")) { output.Add(line); continue; }
                if (Regex.IsMatch(line, @"^(?:成片画幅|画面比例|画幅比例|aspect\s*ratio|【\s*\d+[.．、].*】$|#{1,6}\s+|(?:#+\s*)?SHOT\s*\d+\b|(?:片段)?标题[:：]|(?:最终)?视频提示词\s*[:：]?$)", RegexOptions.IgnoreCase)) continue;
                line = Regex.Replace(line, @"^[—\s]*(\d+\s*[–—\-~至]\s*\d+\s*秒)\s*[|｜]\s*第\s*\d+\s*镜(?=\s|$|[｜|：:])\s*[｜|：:]?\s*", "$1 ");
                line = Regex.Replace(line, @"^第\s*\d+\s*镜\s*[（(]([^）)]+)[）)]", "$1");
                line = Regex.Replace(line, @"[【〖\[]\s*(?:字幕|字卡|caption|subtitles?)\s*[:：][^\]】〗]*[】〗\]]", "", RegexOptions.IgnoreCase);
                line = Regex.Replace(line, @"[^。！？；;\n]*(?:字幕|字卡|叠加(?:文字|字样)|subtitles?|on-screen captions?)[^。！？；;\n]*[。！？；;]?", "", RegexOptions.IgnoreCase);
                line = Prose(line,refs).Trim();
                if (line.Length > 0) output.Add(line);
            }
            return String.Join("\n",output);
        }
    }
}
