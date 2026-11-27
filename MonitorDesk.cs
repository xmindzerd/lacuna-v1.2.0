using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private bool compositionGrid;
        private sealed class MonitorFrame
        {
            internal string Name, Code, Kind, State, Brief, Source;
            internal Func<ImageSource> Picture;
            internal Func<UIElement> Thumb;
            internal Action Trace;
        }

        private TextBlock DeskLabel(string value)
        {
            var label = Muted(value, 11.5, 0);
            label.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            label.Foreground = Brush("AccentText");
            return label;
        }

        private void PaintStageRail(int section)
        {
            var rail = Find<StackPanel>("StageRail");
            rail.Children.Clear();
            bool open = project != null && projectFolder != null;
            Find<Border>("PipelineBar").Visibility = open && section == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (!open || section != 1) return;
            for (int index = -1; index < Stage.All.Length; index++)
            {
                int stage = index;
                string title = stage < 0 ? "项目总览" : Stage.All[index].Name;
                bool current = currentStageSlot == stage + 1;
                var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var number = DeskLabel(Number(index));
                number.Margin = new Thickness(0, 0, 6, 0);
                caption.Children.Add(number);
                caption.Children.Add(Text(SystemWord(title), 13, 0));
                if(stage >= 0 && (stageProgress[stage].Contains("✓") || stageProgress[stage].Contains("待审") || stageProgress[stage].Contains("待核实")))
                    caption.Children.Add(DeskLabel(stageProgress[stage].Contains("✓") ? " ✓" : " •"));

                var link = ActionButton(caption, () => { if (stage < 0) ShowOverview(); else ShowStage(stage); }, false);
                link.Style = Style("StagePillButton");
                link.Tag = current ? "Active" : null;
                link.Margin = new Thickness(0, 0, 3, 0);
                link.Padding = new Thickness(8, 5, 8, 5);
                link.ToolTip = stage < 0 ? "项目当前状态与下一步" : stageProgress[stage];
                ToolTipService.SetShowOnDisabled(link, true);
                AutomationProperties.SetName(link, "制作阶段 " + Number(index) + " " + title);
                rail.Children.Add(link);
            }
        }

        private void AddMonitorReview(string root, ScriptDocument script, AnalysisVersion version, AnalysisResult analysis, AssetWork work, AssetItem selected, FrameworkElement tools)
        {
            var frames = new List<MonitorFrame>();
            var visible = work.Items.Where(i => i.Id == selected.Id || AssetMatches(i)).ToList();
            foreach (var entry in visible)
            {
                var item = entry;
                var attempt = item.Active;
                var lines = (attempt == null || attempt.Path == null ? item.PromptLines : attempt.PromptLines) ?? item.Source.evidence;
                int first = lines == null ? 0 : lines.FirstOrDefault(line => line > 0 && line <= script.Lines.Length);
                var frame = new MonitorFrame { Name = item.Source.name, Code = item.Id, Kind = item.Source.category,
                    State = !AssetProduction.PromptReady(item) ? "提示词待核实" : attempt == null ? "待出图" : attempt.State,
                    Brief = attempt != null && !String.IsNullOrWhiteSpace(attempt.VisualCheck) ? attempt.VisualCheck : item.Source.reason,
                    Source = first > 0 ? script.Lines[first - 1] : "本项暂无可跳转的剧本行。",
                    Thumb = () => AssetThumbnail(root, item.Source.category, attempt) };
                if (first > 0) frame.Trace = () => ShowScriptReader("返回资产监看台", () => ShowAssetPage(item.Id), first, script);
                if (attempt != null && !String.IsNullOrWhiteSpace(attempt.Path))
                    frame.Picture = delegate { return LoadBitmapFile(AssetProduction.Verify(root, attempt), 1440); };
                frames.Add(frame);
            }
            AddMonitor(frames, Math.Max(0, visible.IndexOf(selected)), index => ShowAssetPage(visible[index].Id), tools);
        }

        private TextBlock MonitorCopy(string value, int limit)
        {
            var line = Text(AgentData.Short(value ?? "", limit), 13.5, 4);
            line.TextWrapping = TextWrapping.Wrap;
            line.ToolTip = value;
            return line;
        }

        private void AddMonitor(IList<MonitorFrame> frames, int selected, Action<int> select, FrameworkElement tools = null)
        {
            if (frames == null || frames.Count == 0)
            {
                content.Children.Add(Notice("当前没有可监看的资产项。"));
                return;
            }
            selected = Math.Max(0, Math.Min(frames.Count - 1, selected));
            var frame = frames[selected];
            var viewer = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(16, 10, 12, 10) };
            var grid = new Grid { IsHitTestVisible = false, Visibility = compositionGrid ? Visibility.Visible : Visibility.Collapsed };
            for (int index = 0; index < 3; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.RowDefinitions.Add(new RowDefinition());
            }
            for (int index = 1; index < 3; index++)
            {
                var vertical = new Border { Width = 1, Background = Brushes.White, Opacity = .58, HorizontalAlignment = HorizontalAlignment.Left };
                Grid.SetColumn(vertical, index); Grid.SetRowSpan(vertical, 3); grid.Children.Add(vertical);
                var horizontal = new Border { Height = 1, Background = Brushes.White, Opacity = .58, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(horizontal, index); Grid.SetColumnSpan(horizontal, 3); grid.Children.Add(horizontal);
            }
            var screen = new Grid { Height = 320, Background = Brush("Input"), ClipToBounds = true };
            screen.SizeChanged += delegate {
                if (screen.ActualWidth > 0) screen.Height = Math.Max(1, screen.ActualWidth * .75);
            };
            ImageSource picture = null;
            string fallback = "尚无参考图，可在下方单项制作区准备图片。";
            if (frame.Picture != null)
                try { picture = frame.Picture(); }
                catch (Exception ex) { fallback = "图片不可预览：" + AgentData.Short(ex.Message, 90); }
            if (picture != null)
            {
                var image = new Image { Source = picture, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var overlay = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                overlay.Children.Add(image);
                overlay.Children.Add(grid);
                screen.Children.Add(overlay);
                screen.SizeChanged += delegate {
                    if (picture.Width <= 0 || picture.Height <= 0 || screen.ActualWidth <= 0 || screen.ActualHeight <= 0) return;
                    double ratio = picture.Width / picture.Height;
                    double w = Math.Min(screen.ActualWidth, screen.ActualHeight * ratio);
                    overlay.Width = w;
                    overlay.Height = w / ratio;
                };
            }
            else
            {
                var message = MonitorCopy(fallback, 120);
                message.Margin = new Thickness(20);
                message.Foreground = Brush("TextMuted");
                message.HorizontalAlignment = HorizontalAlignment.Center;
                message.VerticalAlignment = VerticalAlignment.Center;
                screen.Children.Add(message);
            }
            var zoom = ActionButton("放大", () => ShowMonitorZoom(frame.Name, picture), false);
            zoom.IsEnabled = picture != null;
            zoom.Margin = new Thickness(0);
            AddRight(head, zoom);
            var guide = ActionButton(compositionGrid ? "隐藏构图线" : "构图线", delegate { }, false);
            guide.Click += delegate {
                compositionGrid = !compositionGrid;
                grid.Visibility = compositionGrid ? Visibility.Visible : Visibility.Collapsed;
                guide.Content = compositionGrid ? "隐藏构图线" : "构图线";
            };
            guide.IsEnabled = picture != null;
            guide.Margin = new Thickness(0, 0, 8, 0);
            AddRight(head, guide);
            var monitorTitle = Title("画面监看", 15);
            monitorTitle.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(monitorTitle);
            viewer.Children.Add(head);
            viewer.Children.Add(Line());
            viewer.Children.Add(screen);
            viewer.Children.Add(Line());

            var transport = new DockPanel { Margin = new Thickness(16, 8, 12, 8) };
            var next = ActionButton("下一张 ›", () => select(selected + 1), false);
            next.IsEnabled = selected + 1 < frames.Count;
            next.Margin = new Thickness(0);
            var prev = ActionButton("‹ 上一张", () => select(selected - 1), false);
            prev.IsEnabled = selected > 0;
            prev.Margin = new Thickness(0, 0, 8, 0);
            AddRight(transport, next); AddRight(transport, prev);
            var frameCounter = DeskLabel(Number(selected) + " / " + frames.Count.ToString("00"));
            frameCounter.VerticalAlignment = VerticalAlignment.Center;
            transport.Children.Add(frameCounter);
            viewer.Children.Add(transport);
            var display = Panel(viewer);

            var indexPanel = StudioSection(null, "资产索引");
            var inspector = new StackPanel { Margin = new Thickness(18) };
            var topBadges = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            var stateTone = frame.State == "待审图" || frame.State == "提示词待核实" ? "Review" : frame.State == "已通过" ? "Accent" : "Default";
            var badge1 = StatusBadge(frame.Kind, "Default");
            badge1.Margin = new Thickness(0, 0, 6, 4);
            topBadges.Children.Add(badge1);
            var badge2 = StatusBadge(frame.State, stateTone);
            badge2.Margin = new Thickness(0, 0, 6, 4);
            topBadges.Children.Add(badge2);
            inspector.Children.Add(topBadges);
            if(tools != null) inspector.Children.Add(tools);

            var title = Title(frame.Name, 21);
            title.TextWrapping = TextWrapping.Wrap;
            inspector.Children.Add(title);
            inspector.Children.Add(DeskLabel(frame.Code));

            inspector.Children.Add(Line());
            inspector.Children.Add(Muted("自检重点", 12, 12));
            inspector.Children.Add(MonitorCopy(frame.Brief, 120));
            inspector.Children.Add(Muted("剧本依据", 12, 12));
            inspector.Children.Add(MonitorCopy(frame.Source, 96));
            if (frame.Trace != null)
            {
                var traceBtn = ActionButton("回查剧本原文  ›", frame.Trace, false);
                traceBtn.Margin = new Thickness(0, 10, 0, 0);
                traceBtn.HorizontalAlignment = HorizontalAlignment.Left;
                inspector.Children.Add(traceBtn);
            }
            var infoPanel = Panel(inspector);

            var rows = new StackPanel();
            var scroller = new ScrollViewer { Content = rows, MaxHeight = 396, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            indexPanel.Children.Add(scroller);
            for (int pos = 0; pos < frames.Count; pos++)
            {
                int target = pos;
                var other = frames[pos];
                var row = new DockPanel();
                var tick = DeskLabel(Number(pos));
                tick.Width = 24;
                tick.VerticalAlignment = VerticalAlignment.Center;
                AddLeft(row, tick);
                var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var shortName = Text(other.Name, 13.5, 0);
                shortName.FontWeight = target == selected ? FontWeights.SemiBold : FontWeights.Normal;
                shortName.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Children.Add(shortName);
                var subMeta = Muted(other.Kind + " · " + other.State, 11.5, 2);
                if (other.State == "待审图" || other.State == "提示词待核实") subMeta.Foreground = Brush("Review");
                text.Children.Add(subMeta);
                row.Children.Add(text);
                var tile = ActionButton(row, () => select(target), false);
                tile.Style = Style("CardButton");
                tile.Tag = target == selected ? "Active" : null;
                tile.Padding = new Thickness(9, 8, 9, 8);
                tile.Margin = new Thickness(0, 0, 4, 6);
                tile.ToolTip = other.Name + " · " + other.State;
                AutomationProperties.SetName(tile, "参考图 " + other.Code + " " + other.Name);
                rows.Children.Add(tile);
                if (target == selected)
                    scroller.Loaded += delegate
                    {
                        try
                        {
                            if (rows.IsAncestorOf(tile))
                                scroller.ScrollToVerticalOffset(tile.TranslatePoint(new Point(), rows).Y);
                        }
                        catch (InvalidOperationException) { }
                    };
            }
            var detail = StudioColumns(display, infoPanel, 1.85, 1, 630);
            var wall = StudioColumns(indexPanel, detail, .62, 2.38, 960, true);
            content.Children.Add(wall);
        }

        private void ShowMonitorZoom(string name, ImageSource picture)
        {
            if (picture == null) return;
            var dialog = StyleDialog(new Window { Owner = Window, Title = "放大监看 · " + name,
                Width = Math.Min(1060, SystemParameters.WorkArea.Width - 40), Height = Math.Min(780, SystemParameters.WorkArea.Height - 40),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("Input") });
            var shell = new DockPanel { Margin = new Thickness(16) };
            var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var close = ActionButton("关闭预览", () => dialog.Close(), false);
            close.IsCancel = true; close.Margin = new Thickness(0);
            AddRight(bar, close);
            bar.Children.Add(Muted(name + "  ·  按 Esc 关闭", 13, 0));
            DockPanel.SetDock(bar, Dock.Bottom);
            shell.Children.Add(bar);
            var img = new Image { Source = picture, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            shell.Children.Add(img);
            dialog.Content = shell;
            dialog.ShowDialog();
        }
    }
}
