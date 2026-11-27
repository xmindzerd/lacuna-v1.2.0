using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private string[] stageProgress = Enumerable.Repeat("待开始", 7).ToArray();

        private void StageStatus(int slot,string message)
        {
            stageProgress[slot-1]=message;
            var rail=Find<StackPanel>("StageRail");
            var pill=rail.Children.OfType<Button>().ElementAtOrDefault(slot);
            if (pill!=null) pill.ToolTip=message;
        }

        private Window FormDialog(string title, StackPanel form)
        {
            var box = StyleDialog(new Window { Title = title, Owner = Window, Width = 640,
                MaxWidth = SystemParameters.WorkArea.Width - 32, MaxHeight = SystemParameters.WorkArea.Height - 48,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush("Panel"), ShowInTaskbar = false });
            form.Margin = new Thickness(20);
            box.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return box;
        }

        private Button ViewTab(string title, bool selected, Action open)
        {
            var button = ActionButton(title, open, false);
            button.Style = Style("StagePillButton");
            button.Tag = selected ? "Active" : null;
            return button;
        }

        private DockPanel ActionHeader(string label, params UIElement[] actions)
        {
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 5, 0, 10) };
            var tools = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var action in actions) if (action != null) tools.Children.Add(action);
            AddRight(header, tools);
            header.Children.Add(StudioText(label, 14, 5));
            return header;
        }

        private void SetFooter(string label, params UIElement[] actions)
        {
            var footer = Find<ContentControl>("PageFooter");
            footer.Content = new Border { Background = Brush("Panel"), Padding = new Thickness(20, 7, 20, 2),
                Child = ActionHeader(label, actions) };
            footer.Visibility = Visibility.Visible;
        }

        private static string LatestExport(string root)
        {
            var config=FlowCreation.Config(root);
            if(config!=null) {
                var state=FlowCreation.State(root);
                if(!String.IsNullOrWhiteSpace(state.Export)) {
                    string bundle=AgentData.Inside(root,state.Export);
                    if(Directory.Exists(bundle))return bundle;
                }
            }
            string dir = AgentData.Inside(root, @"影片制作\导出");
            return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir)
                .Where(p => !Path.GetFileName(p).StartsWith(".") && File.Exists(Path.Combine(p,"镜头清单.csv")))
                .OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
        }

        private void LoadStageProgress()
        {
            stageProgress = Enumerable.Repeat("待开始", 7).ToArray();
            if (!ScriptAvailable()) return;
            try
            {
                var root = projectFolder;
                var script = AnalysisData.CurrentScript(root);
                var ledger = AnalysisData.Load(root);
                var latest = ledger.Current(script);
                var confirmed = ledger.Confirmed(script);
                stageProgress[0] = latest == null ? "等待分析" : "第 " + latest.Number + " 版" +
                    (latest == confirmed ? "已确认 ✓" : "待审核") + " · " +
                    AnalysisData.ReadFullResult(root,latest).Parts.Sum(p => p.scenes.Count) + " 场";
                if (confirmed == null) return;
                var config=FlowCreation.Config(root);
                var assets = AssetProduction.Load(root, confirmed, AnalysisData.ReadResult(root,confirmed));
                stageProgress[1] = assets.Items.Count(i => i.Active != null && i.Active.State == "已通过") +
                    "/" + assets.Items.Count + " 张已通过" + (assets.DeliveredId == null ? "" : " · 已交接 ✓");
                int blocked=assets.Items.Count(i=>!AssetProduction.PromptReady(i));
                if(blocked>0)stageProgress[1]+=" · "+blocked+" 项待核实";
                var book = Film.Open(root);
                var scenes = book.Scenes.Where(s => s.Included).ToList();
                int segments = scenes.Sum(s => s.Segments.Count);
                stageProgress[2] = scenes.Count(s => s.Shots.Count > 0) + "/" + scenes.Count + " 场已拆镜";
                stageProgress[3] = scenes.Count(s => s.Grouped) + " 场已分段 · " + segments + " 段";
                stageProgress[4] = scenes.Sum(s => s.Segments.Count(g => FilmWriter.Current(book,s,g))) + " 段已定稿";
                var clips = config == null || config.Video == null ? new List<VideoClip>() :
                    VideoDelivery.Plan(root,book,config.Video,null,(s,g) => FilmWriter.Current(book,s,g),true);
                int returned = clips.Select(c => NovelVideo.Read(root,c.Key)).Count(r => r != null && r.Status == "已取回" && !String.IsNullOrEmpty(r.Output) && File.Exists(AgentData.Inside(root,r.Output)));
                stageProgress[5] = "已取回 " + returned + "/" + segments + " 段";
                stageProgress[6] = LatestExport(root) == null ? "尚无导出包" : "已导出交接包 ✓";
            }
            catch (Exception ex) { SetStatus("部分阶段记录暂不可读 · " + AgentData.Short(ex.Message, 160)); }
        }

        private void ShowDelivery()
        {
            string root = projectFolder;
            BeginPage(1, "成片交付与导出汇总", DisplayName(), 7);
            content.Children.Add(ActionHeader("分段视频与交接文件",
                ActionButton("前往 06 视频生成", ShowVideoJobs, false),
                ActionButton("前往 05 导出交接包", () => ShowFilm(4, null, true), false)));
            content.Children.Add(StudioText("当前可导出镜头表、提示词和逐段回片。按剧本顺序自动无缝拼接成片尚待接入。", 14, 5));
            TryAction(delegate {
                var book = Film.Open(root);
                var scenes = book.Scenes.Where(s => s.Included).ToList();
                int total = scenes.Sum(s => s.Segments.Count);
                string export = LatestExport(root);
                content.Children.Add(ActionHeader(export ?? "尚无导出包", export == null ? null :
                    ActionButton("打开导出文件夹", () => OpenSystemFolder(export), false)));
                var cfg = FlowCreation.Config(root);
                var clips = cfg == null || cfg.Video == null ? new List<VideoClip>() :
                    VideoDelivery.Plan(root, book, cfg.Video, null, (s,g) => FilmWriter.Current(book,s,g), true);
                var rows = new StackPanel();
                int returned = 0;
                foreach (var scene in scenes)
                foreach (var segment in scene.Segments)
                {
                    var clip = clips.FirstOrDefault(c => c.Scene == scene.Id && c.First == segment.First);
                    var receipt = clip == null ? null : NovelVideo.Read(root, clip.Key);
                    bool ready = NovelVideo.Returned(root, receipt);
                    if (ready) returned++;
                    rows.Children.Add(ActionHeader(scene.Title + " · 第 " + (scene.Segments.IndexOf(segment)+1) + " 段 · " +
                        (ready ? "已取回" : receipt == null ? (clip == null ? "待定稿或配置视频" : "待生成") : receipt.Status == "已取回" ? "回片文件待核对" : receipt.Status),
                        ready ? ActionButton("监看视频", () => PlayClip(root,receipt), false) : null,
                        ready ? ActionButton("打开回片文件夹", () => OpenClipFile(root,receipt,true), false) : null));
                }
                content.Children.Add(Title("已确认 " + scenes.Count(s => s.Approved) + " 场 · 已取回 " + returned + " / " + total + " 段", 19));
                content.Children.Add(rows);
            });
        }
    }
}
