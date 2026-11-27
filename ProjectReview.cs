using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal enum ReviewScope { Project, Analysis, Assets, Boards, Segments, Prompts }

    internal sealed class ProjectReview
    {
        private readonly List<string> summary = new List<string>();
        private readonly List<string> remaining = new List<string>();

        internal static string Run(string root, ReviewScope scope)
        {
            var report = new ProjectReview();
            try { report.Review(root, scope); }
            catch (IOException e)
            {
                int code = e.HResult & 0xffff;
                report.remaining.Add(code == 32 || code == 33 ? "项目有任务正在使用，请等任务结束后再审。已保存的审核会保留。" : "审核中止：" + e.Message);
            }
            catch (Exception e) { report.remaining.Add("审核中止：" + e.Message); }
            if (report.summary.Count == 0) report.summary.Add("没有新增审核记录。");
            if (report.remaining.Count > 0) report.summary.Add("仍待处理\n" + String.Join("\n", report.remaining));
            return String.Join("\n\n", report.summary);
        }

        private void Review(string root, ReviewScope scope)
        {
            bool all = scope == ReviewScope.Project;
            var source = AnalysisData.CurrentScript(root);
            if (source == null) { remaining.Add("请先导入剧本。"); return; }
            var ledger = AnalysisData.Load(root);
            var version = ledger.Current(source);
            if (version == null) { remaining.Add("请先完成当前剧本的分析。"); return; }
            if (all || scope == ReviewScope.Analysis)
            {
                AnalysisData.Confirm(root, source, version.Id);
                summary.Add("剧本分析第 " + version.Number + " 版已通过。");
            }
            else if (ledger.Confirmed(source) != version)
            {
                remaining.Add("最新剧本分析尚待确认，请先审核剧本分析。"); return;
            }
            if (all || scope == ReviewScope.Assets)
            {
                using (AnalysisData.Lock(root))
                {
                    AssetProduction.RequireCurrent(root, version.Id);
                    var assets = AssetProduction.Load(root, version, AnalysisData.ReadResult(root, version));
                    if (!ReviewAssets(root, assets) && all)
                    {
                        remaining.Add("资产齐全并交接后，可继续审核分镜和提示词。"); return;
                    }
                }
            }
            if (scope == ReviewScope.Assets || scope == ReviewScope.Analysis) return;
            using (new ProjectTaskLease(root, "影片制作"))
            {
                var book = Film.Open(root);
                if (book.AnalysisId != version.Id || book.DeliveryId == null)
                { remaining.Add("当前版本的资产仍待交接。"); return; }
                ReviewFilm(root, book, scope);
            }
        }

        private bool ReviewAssets(string root, AssetWork work)
        {
            int bases = 0, repairs = 0, images = 0;
            bool ready = true;
            foreach (var batch in work.Batches().Select(p => p.Value).Where(b => b.NeedsAction))
            {
                ready = false;
                remaining.Add("出图批次仍有未完成任务，请在资产页继续。");
                foreach (var job in batch.Jobs.Where(j => j.IsBase && j.Done && j.Attempt.State == "待审图"))
                {
                    try { AssetProduction.ReviewBase(root, work, batch.Id, job.Attempt.Id, true); bases++; }
                    catch (IOException e) { remaining.Add(job.Name + "：" + e.Message); }
                    catch (InvalidDataException e) { remaining.Add(job.Name + "：" + e.Message); }
                }
            }
            foreach (var item in work.Items)
            {
                if (!String.IsNullOrWhiteSpace(item.Conflict))
                {
                    var plan = AssetProduction.CurrentRepair(item, item.PromptStyle ?? work.Style);
                    if (plan == null || !plan.canApply)
                    { ready = false; remaining.Add(item.Source.name + "：提示词冲突待核实。"); continue; }
                    AssetProduction.ApplyPromptRepair(item, plan.Revision, plan, plan.Style);
                    repairs++;
                }
                var image = item.Active;
                if (image == null || (image.State != "待审图" && image.State != "已通过"))
                { ready = false; remaining.Add(item.Source.name + "：" + (image == null ? "尚无图片" : image.State)); continue; }
                try { AssetProduction.Verify(root, image); }
                catch (Exception e)
                {
                    if (!(e is IOException || e is InvalidDataException || e is InvalidOperationException || e is UnauthorizedAccessException)) throw;
                    ready = false; remaining.Add(item.Source.name + "：" + e.Message); continue;
                }
                if (image.State == "已通过") continue;
                image.State = "已通过";
                image.ReviewedUtc = AnalysisData.Now();
                image.ReviewNote = "一键全部审核";
                images++;
            }
            if (images + repairs + bases > 0) AssetProduction.Save(root, work);
            summary.Add("本次通过资产图 " + images + " 张、基础图 " + bases + " 张，采纳提示词修复方案 " + repairs + " 项。");
            if (!ready) return false;
            var previous = AssetProduction.ReadDeliveryRecord(root, work);
            if (previous == null || !previous.Items.Select(i => i.ActiveId).SequenceEqual(work.Items.Select(i => i.ActiveId)))
                AssetProduction.Deliver(root, work);
            summary.Add("资产已交接，共 " + work.Items.Count + " 项。");
            return true;
        }

        private void ReviewFilm(string root, FilmBook book, ReviewScope scope)
        {
            bool all = scope == ReviewScope.Project;
            int boards = 0, groups = 0, prompts = 0;
            foreach (var scene in book.Scenes.Where(s => s.Included))
            {
                if (scene.Status == "待续做" || scene.Status == "拆镜中")
                { remaining.Add(scene.Title + "：本场拆镜任务尚待核对。"); continue; }
                if (scene.Shots.Count == 0) { remaining.Add(scene.Title + "：尚未生成分镜。"); continue; }
                try { Film.RequireReady(root, book, scene); }
                catch (Exception e)
                {
                    if (!(e is IOException || e is InvalidDataException || e is InvalidOperationException || e is UnauthorizedAccessException)) throw;
                    remaining.Add(scene.Title + "：" + e.Message); continue;
                }
                if (!scene.Approved)
                {
                    if (!all && scope != ReviewScope.Boards) { remaining.Add(scene.Title + "：请先审核分镜。"); continue; }
                    scene.Approved = true; scene.Status = "已审分镜"; boards++;
                }
                if (scope == ReviewScope.Boards) continue;
                if (!scene.Grouped)
                {
                    if (!all && scope != ReviewScope.Segments) { remaining.Add(scene.Title + "：分段待确认。"); continue; }
                    try
                    {
                        Film.MatchGroups(scene,30,book,VideoPlatform.ProjectModel(root,book.Brief.Ratio));
                        groups++;
                    }
                    catch (Exception e)
                    {
                        if (!(e is IOException || e is InvalidDataException || e is InvalidOperationException || e is UnauthorizedAccessException)) throw;
                        remaining.Add(scene.Title + "：分段失败 · " + e.Message); continue;
                    }
                }
                if (scope == ReviewScope.Segments) continue;
                foreach (var seg in scene.Segments)
                {
                    if (FilmWriter.Current(book, scene, seg)) continue;
                    var draft = seg.Candidates.FirstOrDefault(c => FilmWriter.Matches(book, scene, seg, c.Signature) && !String.IsNullOrWhiteSpace(c.Text) && FilmWriter.Lost(book, scene, seg, c.Text).Length == 0);
                    if (draft == null)
                    { remaining.Add(scene.Title + " 第 " + (scene.Segments.IndexOf(seg) + 1) + " 段：" + (seg.Candidates.Count == 0 ? "尚无候选稿。" : "候选稿已失效或内容不完整。")); continue; }
                    FilmWriter.Accept(book, scene, seg, draft); prompts++;
                }
            }
            if (boards + groups + prompts > 0 || book.SyncRequired) Film.Save(root, book);
            summary.Add("本次确认分镜 " + boards + " 场，确认分段 " + groups + " 场，定稿提示词 " + prompts + " 段。");
        }
    }

    internal sealed partial class Workspace
    {
        private Button ReviewAllButton(ReviewScope scope, Action back)
        {
            string root = projectFolder;
            var button = ActionButton("一键全部审核", () => {}, false);
            button.ToolTip = scope == ReviewScope.Project || scope == ReviewScope.Prompts ? "批量通过已就绪内容。提示词按显示顺序采用第一份有效候选，保留已有定稿。" : "通过当前阶段全部已就绪的内容。";
            button.Click += async delegate
            {
                button.IsEnabled = false;
                button.Content = "正在审核…";
                string report = await Task.Run(() => ProjectReview.Run(root, scope));
                if (!button.IsLoaded || !SameProject(root, projectFolder) || !IsCurrentView) { button.Content="一键全部审核"; button.IsEnabled=true; return; }
                var form = new StackPanel();
                var dialog = FormDialog("批量审核结果", form);
                form.Children.Add(ProjectAnalysisParagraph(report, 15));
                form.Children.Add(ActionButton("确定", () => dialog.Close(), true));
                dialog.ShowDialog();
                back();
            };
            return button;
        }
    }
}
