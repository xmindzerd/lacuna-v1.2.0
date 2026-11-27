using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace OneClickFilm
{
    public sealed class FilmPlace
    {
        public int Page = 2;
        public string Scene;
    }

    public sealed class FilmTaskReceipt
    {
        public string Id, Stage, State, Detail, UpdatedUtc, Skill;
        public List<string> Sessions = new List<string>();
    }

    internal sealed partial class Workspace
    {
        private const string FilmTask = "影片制作";
        private static string FilmNavPath(string root)
        {
            return AgentData.Inside(root, @"影片制作\上次位置.json");
        }

        private static int FilmLastPage(string root)
        {
            var n = AgentData.Read<FilmPlace>(FilmNavPath(root));
            return n == null ? 2 : Math.Max(2, Math.Min(4, n.Page));
        }

        private string[] FilmForm(string title, string[] labels, string[] values, int readOnlyIndex = -1, int preserveFrom = Int32.MaxValue)
        {
            var win = StyleDialog(new Window
            {
                Title = title,
                Owner = Window,
                Width = 740,
                Height = Math.Min(750, 190 + labels.Length * 92),
                MinWidth = 440,
                MinHeight = 260,
                Background = Brush("Panel"),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            });
            var panel = new DockPanel
            {
                Margin = new Thickness(20)
            };
            var actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            panel.Children.Add(actions);
            var form = new StackPanel();
            var boxes = new List<TextBox>();
            for (int i = 0; i < labels.Length; i++)
            {
                form.Children.Add(Muted(labels[i], 14, 6));
                var box = new TextBox
                {
                    Text = values[i] ?? "",
                    IsReadOnly = i == readOnlyIndex,
                    TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true,
                    MinHeight = 36,
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                AutomationProperties.SetName(box, labels[i]);
                boxes.Add(box);
                form.Children.Add(box);
            }

            if (title != null && title.StartsWith("项目与摄影方案", StringComparison.Ordinal) && boxes.Count > 4)
            {
                var picker = new ComboBox
                {
                    ItemsSource = new[]
                    {
                        "选择风格预设",
                        "写实自然光",
                        "写实海岛奇幻",
                        "黑白高反差",
                        "温暖胶片",
                        "清冷夜景",
                        "平面手绘"
                    },
                    SelectedIndex = 0,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                AutomationProperties.SetName(picker, "风格预设");
                picker.SelectionChanged += delegate
                {
                    if (picker.SelectedIndex > 0)
                        boxes[4].Text = (string)picker.SelectedItem;
                };
                form.Children.Insert(0, picker);
            }

            panel.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            actions.Children.Add(ActionButton("取消", () => win.DialogResult = false, false));
            actions.Children.Add(ActionButton("保存", () => win.DialogResult = true, true));
            win.Content = panel;
            return win.ShowDialog() == true ? boxes.Select((b, i) => i >= preserveFrom && b.Text.Replace("\r\n", "\n") == (values[i] ?? "").Replace("\r\n", "\n") ? values[i] : b.Text.Trim()).ToArray() : null;
        }

        private void ShowFilm(int page, string selected = null, bool writing = false)
        {
            string root = projectFolder;
            agentPageProject = root;
            agentProjectPage = Stage.All[page].Name;
            bool enteringPromptStage = page == 4 && !writing && currentStageSlot != 5;
            BeginPage(1, Stage.All[page].Name, DisplayName(), page+1);
            TryAction(delegate
            {
                var b = Film.Open(root);
                if (b.SyncRequired || b.Revision == null)
                    Film.Save(root, b);
                var included=b.Scenes.Where(s=>s.Included).ToList();
                StageStatus(3,included.Count(s=>s.Shots.Count>0)+"/"+included.Count+" 场已拆镜");
                StageStatus(4,included.Count(s=>s.Grouped)+" 场已分段");
                StageStatus(5,included.Sum(s=>s.Segments.Count(g=>FilmWriter.Current(b,s,g)))+" 段已定稿");
                if (enteringPromptStage && b.Scenes.Any(s => s.Included && s.Segments.Any(g => g.Accepted >= 0 && g.Accepted < g.History.Count)))
                {
                    ShowPromptMonitor(null,selected);
                    return;
                }
                var place = AgentData.Read<FilmPlace>(FilmNavPath(root)) ?? new FilmPlace();
                var scene = b.Scenes.FirstOrDefault(x => x.Id == (selected ?? place.Scene)) ?? b.Scenes.FirstOrDefault(x => x.Included) ?? b.Scenes.FirstOrDefault();
                if (place.Page != page || place.Scene != (scene == null ? null : scene.Id))
                    AgentData.Save(FilmNavPath(root), new FilmPlace { Page = page, Scene = scene == null ? null : scene.Id });
                var nav = new WrapPanel();
                content.Children.Add(nav);
                bool overview = page == 2 || page == 3;
                bool detail = overview && filmDetailPage == page && SameProject(root,filmDetailRoot);
                if (overview)
                {
                    nav.Children.Add(ViewTab("整剧汇总", !detail, () => { filmDetailPage=-1; ShowFilm(page,selected); }));
                    nav.Children.Add(ViewTab("逐场工作台", detail, () => OpenFilmScene(root,page,scene == null ? null : scene.Id)));
                }
                else AddPromptTabs(nav,false,selected,null);
                nav.Children.Add(ActionButton("项目与摄影方案", () => TryAction(() => FilmBriefEdit(root, b, page)), false));
                if (scene == null)
                {
                    content.Children.Add(Notice("请先导入剧本，完成全文分析并确认分析版本。"));
                    content.Children.Add(ActionButton("打开剧本分析", () => ShowStage(0), true));
                    return;
                }

                content.Children.Add(Muted((b.Brief.Title ?? DisplayName()) + " · " + b.Brief.Ratio + " · " + b.Brief.Language + " · " + (b.Brief.Target > 0 ? b.Brief.Target + " 秒目标" : "按原文估时"), 15, 7));
                if (b.DeliveryId == null)
                    content.Children.Add(Notice("资产图尚待交接。可先起草分镜，全部图片就绪后可在资产页一键通过。"));
                if (!AssetRunning(root, FilmTask))
                    nav.Children.Add(ReviewAllButton(page == 2 ? ReviewScope.Boards : page == 3 ? ReviewScope.Segments : ReviewScope.Prompts, () => ShowFilm(page)));
                if (overview && !detail && !AssetRunning(root, FilmTask))
                    DrawFilmSummary(root, b, page);
                var list = new ComboBox
                {
                    ItemsSource = b.Scenes.Select(s => (s.Included ? "" : "[资料] ") + s.Title + "　L" + s.First + "–" + s.Last + (s.Approved ? "　已审" : "")).ToList(),
                    SelectedIndex = b.Scenes.IndexOf(scene),
                    MinWidth = 280,
                    Margin = new Thickness(0, 9, 0, 9)
                };
                AutomationProperties.SetName(list, "选择场次");
                list.SelectionChanged += delegate
                {
                    if (list.SelectedIndex >= 0)
                        ShowFilm(page, b.Scenes[list.SelectedIndex].Id);
                };
                if (!overview || detail)
                {
                    var choose = new WrapPanel();
                    int pos = b.Scenes.IndexOf(scene);
                    var prev = ActionButton("‹ 上一场", () => ShowFilm(page,b.Scenes[pos-1].Id), false);
                    var next = ActionButton("下一场 ›", () => ShowFilm(page,b.Scenes[pos+1].Id), false);
                    prev.IsEnabled = pos > 0; next.IsEnabled = pos+1 < b.Scenes.Count;
                    choose.Children.Add(prev); choose.Children.Add(list); choose.Children.Add(next);
                    nav.Children.Insert(Math.Min(2,nav.Children.Count),choose);
                }
                if (AssetRunning(root, FilmTask)) AddAssetProgress(root);
                var receipt = AgentData.Read<FilmTaskReceipt>(FilmReceiptPath(root));
                if (receipt != null && !AssetRunning(root, FilmTask))
                {
                    var trace = new StackPanel();
                    trace.Children.Add(ProjectAnalysisParagraph((receipt.State == "运行中" ? "上次接单尚无结束记录，请先核对本场已保存的产物。" : receipt.Detail) + "\n" + String.Join("\n", receipt.Sessions), 13));
                    content.Children.Add(new Expander { Header = "上次本机任务 · " + receipt.Stage + " · " + (receipt.State == "运行中" ? "待核对" : receipt.State), Content = trace, Foreground = Window.Foreground });
                }

                if (AssetRunning(root, FilmTask))
                {
                    content.Children.Add(Notice("本项目正在制作，可在进度栏停止，已完成的场次会保留。"));
                    return;
                }

                if (overview && !detail) return;
                content.Children.Add(SkillPicker(root, LocalSkills.StageKey(page), Stage.All[page].Name));
                if (page == 2)
                    DrawBoard(root, b, scene);
                else if (page == 3)
                    DrawSegments(root, b, scene);
                else
                    DrawPrompts(root, b, scene);
            });
        }

        private void OpenFilmScene(string root,int page,string scene)
        {
            filmDetailPage=page; filmDetailRoot=root; ShowFilm(page,scene);
        }

        private void AddPromptTabs(WrapPanel row,bool monitor,string scene,Action back)
        {
            row.Children.Add(ViewTab("提示词监看台（图文联动与单段出片）",monitor,() => TryAction(() => ShowPromptMonitor(back,scene))));
            row.Children.Add(ViewTab("提示词编写与多方案修改",!monitor,() => ShowFilm(4,scene,true)));
        }

        private void AddCueCorrection(Panel host,string root,string error,FilmScene hint)
        {
            var match = System.Text.RegularExpressions.Regex.Match(error ?? "", @"原文第\s*(\d+)\s*行缺少已核对的");
            int line;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value,out line)) return;
            host.Children.Add(ActionButton("校订第 " + line + " 行原文对白", () => TryAction(delegate {
                var book = Film.Open(root);
                var scene = book.Scenes.FirstOrDefault(s => s.First <= line && line <= s.Last)
                    ?? book.Scenes.FirstOrDefault(s => hint != null && s.Id == hint.Id);
                if (scene == null) throw new InvalidDataException("当前剧本中找不到该场。");
                var cue = scene.Cues.Where(c => c.Type == "对白").OrderBy(c => Math.Max(0,Math.Max(c.First-line,line-c.Last))).FirstOrDefault();
                if (cue == null) throw new InvalidDataException("该场尚无可校订的对白。");
                OpenFilmScene(root,2,scene.Id);
                // Opening a newly synchronized scene may save a fresh revision.
                book = Film.Open(root); scene = book.Scenes.Single(s=>s.Id==scene.Id);
                EditCue(root,book,scene,scene.Cues.Single(c=>c.Id==cue.Id));
            }),false));
        }

        private int filmDetailPage = -1;
        private string filmDetailRoot;

        private void DrawFilmSummary(string root, FilmBook b, int page)
        {
            var scenes = b.Scenes.Where(s => s.Included).ToArray();
            var pending = scenes.Where(s => s.Shots.Count == 0).ToArray();
            int shots = scenes.Sum(s => s.Shots.Count);
            content.Children.Add(Title("整部剧本 · " + (scenes.Length - pending.Length) + " / " + scenes.Length + " 场已有分镜　" + shots + " 镜", 18));
            if (scenes.Length == 0) { content.Children.Add(Notice("请在逐场设置里将正文纳入拍摄。")); return; }
            if (pending.Length > 0)
            {
                content.Children.Add(ActionButton("一键生成分镜表", () => FilmBoards(root, b, pending), true));
                content.Children.Add(Muted(shots == 0 ? "按剧本顺序生成，每完成一场就保存。" : "已保存的镜头会保留，接着制作剩下的 " + pending.Length + " 场。", 14, 4));
            }
            else
            {
                if (b.DeliveryId == null)
                    content.Children.Add(ActionButton("去通过资产图", () => ShowStage(1), true));
                else if (!scenes.All(s => s.Grouped && s.Approved))
                    content.Children.Add(ActionButton("一键将分镜分段（每段最多 30 秒）", () => TryAction(() =>
                    {
                        Film.GroupProject(root, b);
                        filmDetailPage = -1;
                        ShowFilm(3);
                    }), true));
            }
            if (pending.Length == 0 && scenes.All(s => s.Grouped && s.Approved))
            {
                content.Children.Add(Muted("分段已保存 · 共 " + scenes.Sum(s => s.Segments.Count) + " 段，每段最多 30 秒。", 14, 4));
                content.Children.Add(page == 3 ? ActionButton("进入视频提示词", () => ShowFilm(4), true) :
                    ActionButton("查看整剧分段", () => ShowFilm(3), true));
            }
            int index = 0;
            foreach (var s in scenes)
            {
                var lines = new List<string>();
                if (page == 3 && s.Grouped)
                    foreach (var g in s.Segments)
                        lines.Add("镜 " + (index + g.First + 1) + "–" + (index + g.Last + 1) + "　" + Film.Shots(s, g).Sum(x => x.seconds) + " 秒");
                else
                    foreach (var sh in s.Shots)
                        lines.Add(ShotTitle(sh, index + lines.Count + 1) + "　" + ShotBody(sh));
                index += s.Shots.Count;
                if (!String.IsNullOrWhiteSpace(s.LastError))
                    content.Children.Add(Notice(s.Title + " · 上次暂停：" + s.LastError));
                content.Children.Add(ActionHeader(s.Title,ActionButton("查看/编辑本场 ›", () => OpenFilmScene(root,page,s.Id),false)));
                AddCueCorrection(content,root,s.LastError,s);
                if (lines.Count > 0)
                    content.Children.Add(new Expander { Header = s.Title + "　" + s.Shots.Count + " 镜" + (s.Grouped ? " · " + s.Segments.Count + " 段" : ""), Content = ProjectAnalysisParagraph(String.Join("\n", lines), 14), Foreground = Window.Foreground });
            }
        }

        private void FilmBriefEdit(string root, FilmBook b, int page)
        {
            var br = b.Brief;
            var fixedLanguage = new ProjectStore(null).OpenProject(root).DialogueLanguage;
            bool locked = FlowCreation.IsFlowProject(root);
            var labels = new[] { "片名", "一句话构思", locked ? "画幅（一键成片项目画幅已锁定）" : "画幅（如 9:16 或 16:9）", "目标秒数（0 为随剧本）", "画面风格", "摄影、光线与色调", "对白语言（原文、中文或英文）" };
            var values = new[] { br.Title, br.Idea, br.Ratio, br.Target.ToString(), br.Style, br.Camera, br.Language };
            var v = fixedLanguage == null ? FilmForm("项目与摄影方案", labels, values, locked ? 2 : -1) : FilmForm("项目与摄影方案 · " + fixedLanguage + "短剧", labels.Take(6).ToArray(), values.Take(6).ToArray(), locked ? 2 : -1);
            if (v == null)
                return;
            if (fixedLanguage != null) v = v.Concat(new[] { fixedLanguage }).ToArray();
            if (locked) v[2] = br.Ratio;
            int target;
            if (v[0].Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(v[2], @"^[1-9]\d{0,2}:[1-9]\d{0,2}$") || !Int32.TryParse(v[3], out target) || target < 0 || !new[]
            {
                "原文",
                "中文",
                "英文"
            }.Contains(v[6]))
                throw new InvalidDataException("请核对片名、画幅、非负秒数和对白语言。");
            if (br.Style != v[4] || br.Camera != v[5] || br.Language != v[6] || br.Ratio != v[2] || br.Target != target || br.Idea != v[1])
                Film.Changed(b, null);
            br.Title = v[0];
            br.Idea = v[1];
            if (!locked) br.Ratio = v[2];
            br.Target = target;
            br.Style = v[4];
            br.Camera = v[5];
            br.Language = v[6];
            Film.Save(root, b);
            if (page < 0)
                ShowOverview();
            else
                ShowFilm(page);
        }

        private void FilmAction(WrapPanel row, string text, Action action, bool primary = false)
        {
            row.Children.Add(ActionButton(text, () => TryAction(action), primary));
        }

        private void FilmEdit(string root, FilmBook b, int page, Action change)
        {
            change();
            Film.Save(root, b);
            ShowFilm(page);
        }

        private void DrawBoard(string root, FilmBook b, FilmScene s)
        {
            var row = new WrapPanel();
            content.Children.Add(row);
            FilmAction(row, "角色、造型与地点参考", () => FilmReferences(root, b));
            if (!s.ProductionExcluded) FilmAction(row, s.Included ? "列为背景资料" : "纳入拍摄", () => FilmEdit(root, b, 2, () => s.Included = !s.Included));
            FilmAction(row, "查看本场原文", () => ShowScriptReader("返回本场分镜", () => ShowFilm(2, s.Id), s.First, null));
            if (!s.Included)
                return;
            FilmAction(row, s.Shots.Count > 0 ? "用 Codex 重拆本场" : "用 Codex 拆本场", () => FilmBoards(root, b, new[] { s }), s.Shots.Count==0);
            FilmAction(row, "续做尚未拆镜的场次", () => FilmBoards(root, b, b.Scenes.Where(x => x.Included && x.Shots.Count == 0).ToArray()));
            if (!String.IsNullOrEmpty(s.LastError))
                content.Children.Add(Notice(s.LastError));
            AddCueCorrection(content,root,s.LastError,s);
            var cues = new StackPanel();
            content.Children.Add(new Expander { Header = "原文单元与对白核对 · " + s.Cues.Count, Content = cues, Foreground = Window.Foreground });
            foreach (var cue in s.Cues)
            {
                var c = cue;
                var val = Film.Spoken(c, b.Brief.Language) ? FilmWriter.SourceText(c) : c.Text;
                cues.Children.Add(ActionButton("L" + c.First + "–" + c.Last + "　" + c.Type + "　" + val, () => TryAction(() => EditCue(root, b, s, c)), false));
                if (s.Shots.Count > 0 && Film.Spoken(c, b.Brief.Language))
                {
                    cues.Children.Add(ActionButton("为 L" + c.First + " 单列对白补镜", () => TryAction(() =>
                    {
                        var v = FilmForm("补镜 · " + c.Speaker, new[] { "可见表演或画外音反应", "摄影", "秒数" }, new[] { "延续本场站位，按原对白表演。", "保持当前轴线与视线", "4" });
                        if (v == null)
                            return;
                        int sec;
                        if (!Int32.TryParse(v[2], out sec))
                            throw new InvalidDataException("请输入整数秒。");
                        Film.Pickup(b, s, c, v[0], v[1], sec);
                        Film.Save(root, b);
                        ShowFilm(2, s.Id);
                    }), false));
                }
            }

            foreach (var alert in Film.Continuity(b, s))
                content.Children.Add(Notice(alert));
            foreach (var shot in s.Shots)
            {
                var sh = shot;
                var refs = FilmWriter.Assets(b, sh).Select(a =>
                {
                    var im = Film.Image(a, sh.angle);
                    return a.Name + "（" + (im == null ? "缺图" : im.Angle + ((im.Angle != sh.angle && a.Category == "场景") ? "，回落主图" : "")) + "）";
                });
                content.Children.Add(Title(ShotTitle(sh, s.Shots.IndexOf(sh) + 1), 17));
                content.Children.Add(ProjectAnalysisParagraph("摄影：" + sh.camera, 14));
                content.Children.Add(ProjectAnalysisParagraph(ShotBody(sh) + "\n末态：" + sh.end + "\n" + String.Join("、", refs), 14));
                content.Children.Add(ActionButton("调整此镜", () => TryAction(() => EditShot(root, b, s, sh)), false));
            }

            if (s.Shots.Count > 0)
            {
                var end = new WrapPanel();
                content.Children.Add(end);
                FilmAction(end, "核对覆盖并确认本场分镜", () =>
                {
                    Film.RequireReady(root, b, s);
                    FilmEdit(root, b, 2, () => s.Approved = true);
                }, true);
                FilmAction(end, "进入分段", () => ShowFilm(3, s.Id));
                FilmAction(end, "导出已审镜头表", () =>
                {
                    var folder = FilmWriter.Export(root, b, false);
                    content.Children.Add(ProjectAnalysisParagraph("文件已保存到 " + folder, 14));
                    SetStatus("已导出镜头表 · " + folder);
                });
            }
        }

        private void EditCue(string root, FilmBook b, FilmScene s, FilmCue c)
        {
            var v = FilmForm("原文校订 · L" + c.First + "–" + c.Last, new[] { "类型（对白或画面）", "说话人", "表演或画外音", "核对用文字", "双语版本（中文、英文或留空）" }, new[] { c.Type, c.Speaker, c.Voice, c.Text, c.Language });
            if (v == null)
                return;
            if (!new[]
            {
                "对白",
                "画面"
            }.Contains(v[0]) || v[3].Length == 0 || (v[0] == "对白" && v[1].Length == 0) || !new[]
            {
                "",
                "中文",
                "英文"
            }.Contains(v[4]))
                throw new InvalidDataException("请核对单元类型、文字、语言和说话人。");
            c.Type = v[0];
            c.Speaker = v[1];
            c.Voice = v[2];
            c.Text = v[3];
            c.Language = v[4].Length == 0 ? null : v[4];
            s.LastError = null;
            if (s.Status == "待续做") s.Status = "待拆镜";
            s.Approved = s.Grouped = false;
            Film.Save(root, b);
            ShowFilm(2, s.Id);
        }

        private static string ShotTitle(FilmShot shot, int number)
        {
            return (String.IsNullOrWhiteSpace(shot.code) ? "镜 " + number : shot.code) + "　·　" + shot.seconds + " 秒";
        }

        private static string ShotBody(FilmShot shot)
        {
            var lines = new List<string> { String.IsNullOrWhiteSpace(shot.content) ? shot.action : shot.content };
            if (!String.IsNullOrWhiteSpace(shot.dialogueSlice)) lines.Add("对白分配：" + shot.dialogueSlice);
            if (shot.mustShotRefs != null && shot.mustShotRefs.Count > 0) lines.Add("必需镜头：" + String.Join("、", shot.mustShotRefs));
            return String.Join("\n", lines);
        }

        private void EditShot(string root, FilmBook b, FilmScene s, FilmShot sh)
        {
            var labels = new List<string> { "时长（1–30 秒）", "摄影", "动作", "末态", "参考角度（" + String.Join("、", Film.Angles) + "）",
                "引用资产编号，用逗号分隔\n" + String.Join("　", b.Assets.Select(a => a.Id + " " + a.Name)), "对白分配" };
            var values = new List<string> { sh.seconds.ToString(), sh.camera, sh.action, sh.end, sh.angle,
                String.Join(",", sh.assets ?? new List<string>()), sh.dialogueSlice };
            if (!String.IsNullOrEmpty(sh.content)) { labels.Add("完整分镜内容"); values.Add(sh.content); }
            var v = FilmForm("镜头调整", labels.ToArray(), values.ToArray(), preserveFrom: 6);
            if (v == null)
                return;
            int sec;
            if (!Int32.TryParse(v[0], out sec))
                throw new InvalidDataException("时长需要整数。");
            var candidate = new FilmShot
            {
                code = sh.code, continuation = sh.continuation, mustShotRefs = sh.mustShotRefs,
                dialogueSlice = v[6], content = v.Length > 7 ? v[7] : sh.content,
                first = sh.first,
                last = sh.last,
                seconds = sec,
                camera = v[1],
                action = v[2],
                end = v[3],
                angle = v[4],
                assets = v[5].Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct().ToList()
            };
            var shots = s.Shots.Select(x => x == sh ? candidate : x).ToList();
            Film.CheckShots(b, s, shots);
            s.Shots = shots;
            s.Approved = s.Grouped = false;
            Film.Save(root, b);
            ShowFilm(2, s.Id);
        }

        private void DrawSegments(string root, FilmBook b, FilmScene s)
        {
            if (!s.Included || !s.Approved)
            {
                content.Children.Add(Notice("请在分镜页确认本场属于拍摄范围并通过审核。"));
                return;
            }

            var groups = s.Grouped ? s.Segments : Film.Group(s, 30);
            int total = 0;
            foreach (var g in groups)
            {
                int seconds = Film.Shots(s, g).Sum(sh => sh.seconds);
                content.Children.Add(Muted("第 " + (groups.IndexOf(g) + 1) + " 段　镜 " + (g.First + 1) + "–" + (g.Last + 1) + "　" + total + "–" + (total + seconds) + " 秒", 15, 7));
                total += seconds;
            }

            content.Children.Add(Muted("本场 " + total + " 秒 · " + (s.Grouped ? "分段已确认" : "待确认"), 14, 8));
            var row = new WrapPanel();
            content.Children.Add(row);
            FilmAction(row, "确认分段上限", () =>
            {
                var target=VideoPlatform.ProjectModel(root,b.Brief.Ratio);
                int upper=target==null?30:target.Max;
                var v = FilmForm("确认分段", new[] { "每段时长上限（秒）" }, new[] { upper.ToString() });
                if (v == null)
                    return;
                int max;
                if (!Int32.TryParse(v[0], out max))
                    throw new InvalidDataException("请输入整数。");
                Film.RequireReady(root, b, s);
                if(max>upper || target!=null && max<target.Min)throw new InvalidDataException("该模型的分段上限应设为 "+(target==null?1:target.Min)+"–"+upper+" 秒。");
                FilmEdit(root,b,3,()=>Film.MatchGroups(s,max,b,target));
            }, true);
            FilmAction(row, "进入视频提示词", () => ShowFilm(4, s.Id));
        }

        private void DrawPrompts(string root, FilmBook b, FilmScene s)
        {
            var pendingScenes = b.Scenes.Where(sc=>sc.Included && sc.Approved && sc.Grouped &&
                sc.Segments.Any(g=>!FilmWriter.Current(b,sc,g) && !g.Candidates.Any(c=>FilmWriter.Matches(b,sc,g,c.Signature)))).ToList();
            if (pendingScenes.Count > 0)
                content.Children.Add(ActionButton("生成全剧待写提示词",()=>FilmRun(root,4,(exe,model,skill,token,progress)=>{
                    b.FinalSkill = skill == null ? null : skill.Reference;
                    foreach(var sc in pendingScenes)
                    {
                        Film.RequireReady(root,b,sc);
                        if (FilmWriter.EnsureSceneGlobalPrompt(exe,model,skill,b,sc,token,progress))
                            lock (b) Film.Save(root,b);
                        var pending=sc.Segments.Where(g=>!FilmWriter.Current(b,sc,g) && !g.Candidates.Any(c=>FilmWriter.Matches(b,sc,g,c.Signature))).ToList();
                        for(int n=0;n<pending.Count;n+=4) {
                            token.ThrowIfCancellationRequested();
                            var group=pending.Skip(n).Take(4).ToList();
                            var rows=FilmWriter.Write(exe,model,skill,b,sc,group,"",false,token,progress);
                            for(int j=0;j<group.Count;j++)group[j].Candidates=new List<FilmPrompt>{rows[j]};
                            Film.Save(root,b);
                        }
                    }
                }),true));
            if (!s.Included || !s.Approved || !s.Grouped)
            {
                content.Children.Add(Notice("请先审核本场分镜并确认分段。"));
                return;
            }

            var header = new WrapPanel();
            content.Children.Add(header);
            if (!String.IsNullOrEmpty(s.GlobalPrompt))
                content.Children.Add(new Expander { Header = "本场全局提示词", Content = new TextBox { Text = s.GlobalPrompt,
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
            FilmAction(header, "生成本场待写提示词", () => FilmPrompts(root, b, s, null, false, ""), false);
            foreach (var g in s.Segments)
            {
                var seg = g;
                content.Children.Add(Title("第 " + (s.Segments.IndexOf(g) + 1) + " 段　·　镜 " + (g.First + 1) + "–" + (g.Last + 1) + "　·　" + (FilmWriter.Current(b, s, g) ? "已定稿" : "待定稿"), 17));
                var row = new WrapPanel();
                content.Children.Add(row);
                FilmAction(row, "三种导演方案", () =>
                {
                    var v = FilmForm("导演批注", new[] { "想调整的构图、光线、运动或表演" }, new[] { "" });
                    if (v != null)
                        FilmPrompts(root, b, s, g, true, v[0]);
                });
                if (g.Candidates.Any(c => !FilmWriter.Matches(b, s, g, c.Signature)))
                    content.Children.Add(Notice("上游资料已有变动，旧候选保留供查阅，请重新生成。"));
                if (g.Accepted >= 0)
                    content.Children.Add(ProjectAnalysisParagraph(g.History[g.Accepted].Text, 14));
                if (g.Accepted >= 0 || g.Candidates.Count > 0)
                {
                    var copy = g.Candidates.FirstOrDefault();
                    string basis = g.Accepted >= 0 ? g.History[g.Accepted].Text : copy.Text;
                    FilmAction(row, "修改当前提示词", () =>
                    {
                        var val = FilmForm("修改当前提示词", new[] { "提示词全文" }, new[] { basis });
                        if (val == null)
                            return;
                        var candidate = new FilmPrompt
                        {
                            Text = val[0],
                            Signature = FilmWriter.Signature(b, s, g),
                            Skill = "人工修订"
                        };
                        FilmEdit(root, b, 4, () => FilmWriter.Accept(b, s, g, candidate));
                    });
                }

                foreach (var c in g.Candidates)
                {
                    var draft = c;
                    var body = new StackPanel();
                    body.Children.Add(ProjectAnalysisParagraph(c.Text, 14));
                    body.Children.Add(ActionButton("采纳此稿", () => TryAction(() => FilmEdit(root, b, 4, () => FilmWriter.Accept(b, s, seg, draft))), false));
                    body.Children.Add(ActionButton("编辑后采纳", () => TryAction(() =>
                    {
                        var v = FilmForm("修改提示词", new[] { "提示词全文" }, new[] { draft.Text });
                        if (v == null)
                            return;
                        FilmEdit(root, b, 4, () => FilmWriter.Accept(b, s, seg, new FilmPrompt { Text = v[0], Signature = draft.Signature, Skill = draft.Skill }));
                    }), false));
                    content.Children.Add(new Expander { Header = "候选 " + (g.Candidates.IndexOf(c) + 1) + "　" + AgentData.Short(c.Text, 48), Content = body, Foreground = Window.Foreground });
                }

                if (g.History.Count > 0)
                {
                    var hist = new StackPanel();
                    content.Children.Add(new Expander { Header = "已采纳的历史版本 · " + g.History.Count, Content = hist, Foreground = Window.Foreground });
                    foreach (var hp in g.History)
                    {
                        var old = hp;
                        hist.Children.Add(ProjectAnalysisParagraph(hp.Text, 13));
                        hist.Children.Add(ActionButton("恢复此版本 · " + hp.Utc, () => TryAction(() => FilmEdit(root, b, 4, () => FilmWriter.Accept(b, s, seg, old))), false));
                    }
                }
            }

            var footer = new WrapPanel();
            content.Children.Add(footer);
            foreach (bool inc in new[]
            {
                true,
                false
            }

            )
            {
                bool with = inc;
                FilmAction(footer, inc ? "导出镜头表与最终提示词" : "导出简版镜头表", () =>
                {
                    if(with && FlowCreation.IsRegularOutput(root)){StartFlow(root,null);return;}
                    var folder = FilmWriter.Export(root, b, with);
                    content.Children.Add(ProjectAnalysisParagraph("文件已保存到 " + folder, 14));
                    SetStatus("文件已保存到 " + folder);
                }, false);
            }

            var plan = new StackPanel();
            int pos = 0;
            foreach (var scene in b.Scenes.Where(x => x.Included))
            {
                var sec = scene.Shots.Sum(sh => sh.seconds);
                plan.Children.Add(Muted(scene.Title + "　" + pos + "–" + (pos + sec) + " 秒　" + scene.Segments.Count + " 段待生成视频", 13, 4));
                pos += sec;
            }

            content.Children.Add(new Expander { Header = "按剧本顺序预览交接时间线", Content = plan, Foreground = Window.Foreground });
        }

        private delegate void FilmJob(string exe, string model, SelectedSkill skill, CancellationToken cancel, Action<string> progress);
        private static string FilmReceiptPath(string root)
        {
            return AgentData.Inside(root, @"影片制作\最近任务.json");
        }

        private static void RecordFilmEvent(string root, FilmTaskReceipt receipt, string detail)
        {
            lock (receipt)
            {
                var match = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"本机 Codex 已接收 · ([\w-]+)");
                if (match.Success && !receipt.Sessions.Contains(match.Groups[1].Value))
                    receipt.Sessions.Add(match.Groups[1].Value);
                receipt.Detail = detail;
                receipt.UpdatedUtc = AnalysisData.Now();
                AgentData.Save(FilmReceiptPath(root), receipt);
            }
        }

        private async void FilmRun(string root, int page, FilmJob job, string operation = null)
        {
            if (AssetRunning(root, FilmTask) || exitAfterCodexTask)
            {
                SetStatus("请等待正在运行的影片任务。");
                return;
            }

            if (codexStatus == null || codexStatus.State != CodexState.SignedIn)
            {
                SetStatus(CodexAuthMode.IsApi ? "请在设置中确认 Responses API 的项目 Key 和模型。" : "请先在设置中确认本机 Codex 已登录。");
                RefreshCodex();
                return;
            }

            SelectedSkill skill;
            if (!ResolveSkill(root, operation ?? LocalSkills.StageKey(page), out skill))
                return;
            ProjectTaskLease lease = null;
            if (!TryAction(() => lease = new ProjectTaskLease(root, FilmTask)))
                return;
            var task = new AssetTask
            {
                Root = root,
                Name = FilmTask,
                Status = "正在向本机 Codex 发送任务"
            };
            var exe = codexStatus.Path;
            var receipt = new FilmTaskReceipt
            {
                Id = Film.Id(),
                Stage = Stage.All[page].Name,
                Skill = skill == null ? null : skill.Reference.Name,
                State = "运行中"
            };
            string msg = null;
            try
            {
                BeginAssetTask(task);
                ShowFilm(page);
                UpdateCodexDisplay();
                RecordFilmEvent(root, receipt, task.Status);
                await Task.Run(() => job(exe, AgentData.Options().Model, skill, task.Cancel.Token, detail =>
                {
                    RecordFilmEvent(root, receipt, detail);
                    Window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (assetTasks.Contains(task))
                            SetAssetProgress(root, detail, 0, 0, FilmTask);
                    }));
                }));
                receipt.State = "已完成";
                msg = "影片制作结果已保存。";
            }
            catch (Exception e)
            {
                receipt.State = task.Cancel.IsCancellationRequested ? "已停止" : "待续做";
                msg = task.Cancel.IsCancellationRequested ? "已停止，之前完成的工作已保存。" : AgentData.Short(e.Message, 750);
            }
            finally
            {
                try
                {
                    RecordFilmEvent(root, receipt, msg);
                }
                catch (IOException e)
                {
                    msg += " · 回执保存失败：" + e.Message;
                }
                catch (UnauthorizedAccessException e)
                {
                    msg += " · 回执保存失败：" + e.Message;
                }

                lease.Dispose();
                EndAssetTask(task);
                if (SameProject(root, assetProgressProject)) assetProgressText = msg;
                task.Cancel.Dispose();
                if (SameProject(root, projectFolder) && agentProjectPage == Stage.All[FilmLastPage(root)].Name && assetProgressPanel != null && content.Children.Contains(assetProgressPanel))
                    ShowFilm(FilmLastPage(root));
                if (SameProject(root, projectFolder))
                    SetStatus(msg);
                UpdateCodexDisplay();
                if (exitAfterCodexTask)
                {
                    exitAfterCodexTask = false;
                    FinishExit();
                }
            }
        }

        private void FilmBoards(string root, FilmBook b, FilmScene[] scenes)
        {
            if (scenes.Length == 0)
            {
                SetStatus("全部拍摄场次都有分镜稿。");
                return;
            }

            FilmRun(root, 2, (exe, model, skill, cancel, progress) =>
            {
                foreach (var s in scenes)
                {
                    cancel.ThrowIfCancellationRequested();
                    s.Status = "拆镜中";
                    s.LastError = null;
                    Film.Save(root, b);
                    try
                    {
                        var target=VideoPlatform.ProjectModel(root,b.Brief.Ratio);
                        var shots = FilmWriter.Board(exe, model, skill, b, s, cancel, msg => progress(s.Title + " · " + msg),target);
                        progress(s.Title + " · 正在保存 " + shots.Count + " 个镜头");
                        s.Shots = shots;
                        s.Approved = s.Grouped = false;
                        s.Status = "待审分镜";
                        s.BoardSkill = skill == null ? null : skill.Reference.Name;
                        Film.Save(root, b);
                    }
                    catch (Exception e)
                    {
                        s.Status = "待续做";
                        s.LastError = e.Message;
                        Film.Save(root, b);
                        throw;
                    }
                }
            });
        }

        private void FilmPrompts(string root, FilmBook b, FilmScene s, FilmSegment one, bool alternatives, string notes)
        {
            Film.RequireReady(root, b, s);
            var groups = one == null ? s.Segments.Where(g => !FilmWriter.Current(b, s, g) && !g.Candidates.Any(c => FilmWriter.Matches(b, s, g, c.Signature))).ToList() : new List<FilmSegment>
            {
                one
            };
            if (groups.Count == 0)
            {
                SetStatus("当前分段已有候选或已定稿，可展开查看。");
                return;
            }

            FilmRun(root, 4, (exe, model, skill, token, progress) =>
            {
                b.FinalSkill = skill == null ? null : skill.Reference;
                if (FilmWriter.EnsureSceneGlobalPrompt(exe,model,skill,b,s,token,progress))
                    lock (b) Film.Save(root,b);
                for (int i = 0; i < groups.Count; i += 4)
                {
                    var batch = groups.Skip(i).Take(4).ToList();
                    var rows = FilmWriter.Write(exe, model, skill, b, s, batch, notes, alternatives, token, progress);
                    if (alternatives)
                        one.Candidates = rows;
                    else
                        for (int j = 0; j < batch.Count; j++)
                            batch[j].Candidates = new List<FilmPrompt>
                            {
                                rows[j]
                            };
                    Film.Save(root, b);
                }
            });
        }

        private void FilmReferences(string root, FilmBook b, string selected = null)
        {
            BeginPage(1, "分镜补充角色、造型与机位参考", b.Brief.Title, 3);
            AddPageBack("返回分镜工作台", () => OpenFilmScene(root,2,null));
            var row = new WrapPanel();
            content.Children.Add(row);
            FilmAction(row, "新增无对白角色或地点", () =>
            {
                var v = FilmForm("补录制作资产", new[] { "名字", "类型（人物、场景或道具）", "角色身份或地点", "造型或地点描述" }, new[] { "", "人物", "", "" });
                if (v == null)
                    return;
                if (v[0].Length == 0 || !new[]
                {
                    "人物",
                    "场景",
                    "道具"
                }.Contains(v[1]))
                    throw new InvalidDataException("请填写名字和类型。");
                var a = new FilmAsset
                {
                    Id = "F" + Film.Id().Substring(0, 8),
                    Name = v[0],
                    Category = v[1],
                    Identity = v[2],
                    Look = v[3]
                };
                b.Assets.Add(a);
                Film.Save(root, b);
                FilmReferences(root, b, a.Id);
            });
            FilmAction(row, "打开资产制作与审图", () => ShowStage(1));
            if (b.Assets.Count == 0)
                return;
            var asset = b.Assets.FirstOrDefault(a => a.Id == selected) ?? b.Assets[0];
            var select = new ComboBox
            {
                ItemsSource = b.Assets.Select(a => a.Id + "　" + a.Category + "　" + a.Name).ToList(),
                SelectedIndex = b.Assets.IndexOf(asset)
            };
            AutomationProperties.SetName(select, "选择角色或地点");
            select.SelectionChanged += delegate
            {
                if (select.SelectedIndex >= 0)
                    FilmReferences(root, b, b.Assets[select.SelectedIndex].Id);
            };
            content.Children.Add(select);
            content.Children.Add(Muted("身份：" + asset.Identity + "　演员：" + asset.Actor + "　造型 / 地点说明：" + asset.Look, 14, 8));
            var tools = new WrapPanel();
            content.Children.Add(tools);
            FilmAction(tools, "编辑演员与造型", () =>
            {
                var v = FilmForm("演员、造型与地点", new[] { "显示名", "角色或地点身份", "演员或预设名称", "造型或地点描述" }, new[] { asset.Name, asset.Identity, asset.Actor, asset.Look });
                if (v == null)
                    return;
                if (v[0].Length == 0)
                    throw new InvalidDataException("请填写显示名。");
                asset.Name = v[0];
                asset.Identity = v[1];
                asset.Actor = v[2];
                asset.Look = v[3];
                Film.Changed(b, asset.Id);
                Film.Save(root, b);
                FilmReferences(root, b, asset.Id);
            });
            if (asset.Category == "人物")
                FilmAction(tools, "新增此角色的造型", () =>
                {
                    var values = FilmForm("新增角色造型", new[] { "造型名称", "造型说明" }, new[] { asset.Identity + " · 新造型", "" });
                    if (values == null)
                        return;
                    if (values[0].Length == 0)
                        throw new InvalidDataException("请填写造型名称。");
                    var look = new FilmAsset
                    {
                        Id = "F" + Film.Id().Substring(0, 8),
                        Name = values[0],
                        Identity = asset.Identity,
                        Actor = asset.Actor,
                        Look = values[1],
                        Category = "人物"
                    };
                    b.Assets.Add(look);
                    Film.Save(root, b);
                    FilmReferences(root, b, look.Id);
                });
            if (asset.Category == "人物")
                FilmAction(tools, "Codex 编写本造型", () => FilmRun(root, 2, (exe, model, skill, cancel, progress) =>
                {
                    var src = b.Scenes.SelectMany(s => s.Cues).Where(c => !String.IsNullOrWhiteSpace(asset.Identity) && (c.Text.Contains(asset.Identity) || (c.Speaker ?? "").Contains(asset.Identity))).Select(c => c.First + " " + c.Text);
                    var text = FilmWriter.Wardrobe(exe, model, skill, asset, src, cancel, progress);
                    asset.Look = text;
                    Film.Changed(b, asset.Id);
                    Film.Save(root, b);
                }));
            var angle = new ComboBox
            {
                ItemsSource = asset.Category == "场景" ? (IEnumerable<string>)Film.Angles : new[]
                {
                    "主图"
                },
                SelectedIndex = 0,
                MinWidth = 115
            };
            AutomationProperties.SetName(angle, "参考图角度");
            tools.Children.Add(angle);
            FilmAction(tools, "导入参考图", () =>
            {
                var file = new OpenFileDialog
                {
                    Filter = "图片|*.png;*.jpg;*.jpeg;*.webp"
                };
                if (file.ShowDialog(Window) != true)
                    return;
                Film.Import(root, asset, (string)angle.SelectedItem, file.FileName);
                Film.Changed(b, asset.Id);
                Film.Save(root, b);
                FilmReferences(root, b, asset.Id);
            });
            var channels = AssetChannels.Load();
            var channel = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Codex 内置出图"
                }.Concat(channels.Select(c => c.Name)).ToList(),
                SelectedIndex = 0,
                MinWidth = 132
            };
            AutomationProperties.SetName(channel, "参考图渠道");
            tools.Children.Add(channel);
            FilmAction(tools, "生成或重画所选角度", () => MakeFilmImage(root, b, asset, (string)angle.SelectedItem, channel.SelectedIndex <= 0 ? null : channels[channel.SelectedIndex - 1]));
            if (asset.PendingImage != null)
                FilmAction(tools, "核对上次出图", () =>
                {
                    var folder = AgentMedia.JobFolder(root, new AgentJob { Id = asset.PendingImage });
                    var receipt = AgentMedia.ReadJobReceipt(folder, null);
                    if (receipt != null && receipt.State == "已取回")
                    {
                        FinishFilmImage(root, b, asset, AgentData.Inside(folder, receipt.Output));
                    }
                    else if (AgentMedia.ConfirmedFailure(folder))
                    {
                        asset.PendingImage = null;
                        Film.Save(root, b);
                    }
                    else
                        throw new InvalidOperationException("这次出图仍未核实，暂缓重新提交。任务号 " + asset.PendingImage);
                    FilmReferences(root, b, asset.Id);
                });
            var history = new StackPanel();
            content.Children.Add(new Expander { Header = "参考图版本 · " + asset.Images.Count, Content = history, IsExpanded = true, Foreground = Window.Foreground });
            foreach (var img in asset.Images.AsEnumerable().Reverse())
            {
                var im = img;
                history.Children.Add(Muted(im.Angle + " · " + (im == Film.Image(asset, im.Angle) ? "当前版本" : "历史 / 待复核") + "　" + AgentData.Short(im.Digest, 12), 13, 4));
                history.Children.Add(AssetThumbnail(root, asset.Category, new AssetAttempt { Path = im.Path }));
            }
        }

        private static void FinishFilmImage(string root, FilmBook b, FilmAsset asset, string path)
        {
            var main = Film.Image(asset, "主图");
            if (asset.PendingAngle != "主图" && (main == null || main.Digest != asset.PendingParent))
                throw new InvalidOperationException("主图已换版，请核对原任务的图像来源。");
            string fitted = null;
            try
            {
                if (ImageOutputSettings.Ratio(asset.PendingTargetSize) == ImageOutputSettings.DefaultAssetRatio)
                {
                    var original = new AssetAttempt();
                    AssetProduction.Inspect(path, original);
                    if ((long)original.Width * 3 != (long)original.Height * 4)
                    {
                        fitted = AgentData.Inside(root, Path.Combine("影片制作", Film.Id() + ".png"));
                        ImageOutputSettings.SaveFourThree(path, fitted, asset.PendingTargetSize);
                    }
                }
                Film.Import(root, asset, asset.PendingAngle, fitted ?? path);
            }
            finally { if (fitted != null && File.Exists(fitted)) File.Delete(fitted); }
            asset.PendingTargetSize = null;
            asset.PendingImage = null;
            Film.Changed(b, asset.Id);
            Film.Save(root, b);
        }

        private void MakeFilmImage(string root, FilmBook b, FilmAsset asset, string angle, AssetChannel channel)
        {
            if (asset.PendingImage != null)
                throw new InvalidOperationException("请先核对上次出图。");
            var main = Film.Image(asset, "主图");
            if (angle != "主图" && main == null)
                throw new InvalidOperationException("请先制作地点主图。");
            var opts = AgentData.Options();
            if (channel != null)
            {
                if (main != null && channel.Driver != FlowChannels.Dreamina && !(channel.Arguments ?? "").Contains("{reference}"))
                    throw new InvalidOperationException("该渠道未设置参考图参数。");
                opts = channel.Options(opts);
            }

            string route = channel == null ? "builtin" : "cli";
            string agreed;
            if (!Prompt("确认制作参考图", asset.Name + " · " + angle + " · " + (channel == null ? "Codex 内置图片生成" : channel.Name) + "\n本次提交一张图片。", "开始制作", "取消", false, out agreed))
                return;
            int timeout = ImageOutputSettings.TimeoutMinutes(Sys.ImageTimeoutMinutes) * 60000;
            FilmRun(root, 2, (exe, model, skill, token, progress) =>
            {
                AgentMedia.Budget(opts, "图片", 1);
                var attempt = new AssetAttempt
                {
                    Id = Film.Id(),
                    Prompt = "制作影视参考图，" + asset.Category + "：" + asset.Name + "，" + asset.Look + "。画风：" + b.Brief.Style + "。机位：" + angle + (asset.Category == "人物" ? "，清楚呈现面部与造型。" : "，保持场地结构与主图一致。")
                };
                asset.PendingTargetSize = ImageOutputSettings.DefaultSize(channel);
                attempt.Prompt = ImageOutputSettings.ApplySize(attempt.Prompt, asset.PendingTargetSize);
                asset.PendingImage = attempt.Id;
                asset.PendingAngle = angle;
                asset.PendingParent = main == null ? null : main.Digest;
                Film.Save(root, b);
                var pics = main == null ? new List<string>() : new List<string>
                {
                    AgentData.Inside(root, main.Path)
                };
                var result = PerformImage(root, route, opts, exe, attempt, pics, ImageOutputSettings.ProviderSize(channel, asset.PendingTargetSize), skill, timeout, token);
                if (result.Path == null)
                {
                    if (result.PlatformFailed || !result.MayHaveSubmitted)
                    {
                        asset.PendingImage = null;
                        Film.Save(root, b);
                    }

                    throw new InvalidOperationException(result.Error);
                }

                FinishFilmImage(root, b, asset, AgentData.Inside(AgentData.DirectoryFor(root), result.Path));
            }, ImageOutputSettings.SkillOperation);
        }
    }
}
