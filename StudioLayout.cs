using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private Grid StudioColumns(FrameworkElement first, FrameworkElement second, double aWeight, double bWeight, double breakpoint, bool narrowSecondFirst = false)
        {
            var grid = new Grid();
            var left = new ColumnDefinition { Width = new GridLength(aWeight, GridUnitType.Star) };
            var right = new ColumnDefinition { Width = new GridLength(bWeight, GridUnitType.Star) };
            grid.ColumnDefinitions.Add(left);
            grid.ColumnDefinitions.Add(right);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var one = new Border { Child = first };
            var two = new Border { Child = second, Margin = new Thickness(18, 0, 0, 0) };
            Grid.SetColumn(two, 1);
            grid.Children.Add(one);
            grid.Children.Add(two);
            bool compact = false;
            grid.SizeChanged += delegate {
                bool next = grid.ActualWidth > 0 && grid.ActualWidth < breakpoint;
                if (next == compact) return;
                compact = next;
                right.Width = compact ? new GridLength(0) : new GridLength(bWeight, GridUnitType.Star);
                Grid.SetColumn(two, compact ? 0 : 1);
                Grid.SetRow(one, compact && narrowSecondFirst ? 1 : 0);
                Grid.SetRow(two, compact && !narrowSecondFirst ? 1 : 0);
                one.Margin = compact && narrowSecondFirst ? new Thickness(0, 16, 0, 0) : new Thickness(0);
                two.Margin = compact ? new Thickness(0, narrowSecondFirst ? 0 : 16, 0, 0) : new Thickness(18, 0, 0, 0);
            };
            return grid;
        }

        private StackPanel StudioSection(string tag, string name)
        {
            var stack = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 2, 0, 12) };
            var title = Title(name, 19);
            header.Children.Add(title);
            stack.Children.Add(header);
            return stack;
        }

        private TextBlock StudioText(string value, double size, double space)
        {
            var text = Muted(value, size, space);
            text.TextWrapping = TextWrapping.Wrap;
            return text;
        }

        private void DrawStudioHome(List<ProjectEntry> entries)
        {
            var mast = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 22) };
            var commands = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            commands.Children.Add(ActionButton("一键成片", OpenFlowWizard, true));
            commands.Children.Add(ActionButton("打开项目", OpenProject, false));
            commands.Children.Add(ActionButton("问题反馈", ShowFeedbackCenter, false));
            AddRight(mast, commands);

            var headings = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headings.Children.Add(Title("制作工作台", 28));
            headings.Children.Add(Muted("剧本分析 · 资产设定 · 分镜分段 · 视频提示词与出片", 13.5, 5));
            mast.Children.Add(headings);
            content.Children.Add(mast);

            var projects = StudioSection(null, "最近项目");
            var feature = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            var last = entries.Find(item => SameFolder(item.Folder, preferences.LastProject));

            var heroTop = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            heroTop.Children.Add(StatusBadge(last == null ? "准备开始" : "上次制作", "Accent"));
            if (last != null && last.Progress != null)
            {
                var catBadge = StatusBadge(last.Progress.CategoryLabel, "Default");
                catBadge.Margin = new Thickness(8, 0, 0, 0);
                heroTop.Children.Add(catBadge);
            }
            feature.Children.Add(heroTop);

            var name = Title(last == null ? "从一份剧本开始" : last.Name, 25);
            name.TextWrapping = TextWrapping.Wrap;
            feature.Children.Add(name);

            string lastSubtitle = last == null
                ? "新建一键成片项目，或打开已有项目文件夹继续制作。"
                : last.Progress == null
                    ? last.Error
                    : (last.Progress.Automatic ? FlowCreation.Status(last.Folder) : ScriptState(last.Progress, last.Folder));
            feature.Children.Add(StudioText(lastSubtitle, 14, 7));

            var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            if (last != null && last.Progress != null)
            {
                actions.Children.Add(ActionButton("继续上次制作", () => EnterProject(last, true), true));
                actions.Children.Add(ActionButton("项目总览", () => EnterProject(last, false), false));
            }
            else if (last != null) actions.Children.Add(ActionButton("重新定位", () => Relocate(last), true));
            else actions.Children.Add(ActionButton("创建一键成片项目", OpenFlowWizard, true));
            feature.Children.Add(actions);

            var card = Panel(feature);
            card.Background = Brush("SelectedNavigation");
            card.BorderBrush = Brush("Focus");
            if (last != null && last.Progress != null)
            {
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.ToolTip = "打开「" + last.Name + "」项目总览";
                card.MouseLeftButtonUp += delegate { EnterProject(last, false); };
            }
            projects.Children.Add(card);

            var library = new DockPanel { Margin = new Thickness(0, 20, 0, 10) };
            var allLink = ActionButton("查看所有项目  ›", ShowProjectList, false);
            allLink.Style = Style("GhostButton");
            allLink.Margin = new Thickness(0);
            AddRight(library, allLink);
            var shelfTitle = Title("项目架", 16);
            shelfTitle.VerticalAlignment = VerticalAlignment.Center;
            library.Children.Add(shelfTitle);
            projects.Children.Add(library);

            int more = 0;
            foreach (var entry in entries)
                if (entry != last) { projects.Children.Add(ProjectRow(entry, true)); more++; }
            if (more == 0) projects.Children.Add(StudioText("其他最近打开的项目会列在这里。", 13, 4));

            content.Children.Add(projects);
        }

        private Button StageTile(int index, string name, string phase, bool active, Action go)
        {
            var body = new StackPanel { Margin = new Thickness(2) };
            var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var stepPill = new Border
            {
                Background = Brush(active ? "Accent" : "PanelLift"),
                BorderBrush = Brush(active ? "Accent" : "Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2)
            };
            var stepText = Text(Number(index), 11.5, 0);
            stepText.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            stepText.FontWeight = FontWeights.SemiBold;
            stepText.Foreground = active ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#191632")) : Brush("AccentText");
            stepPill.Child = stepText;
            AddLeft(topRow, stepPill);

            if (active)
            {
                var currentTag = StatusBadge("当前阶段", "Accent");
                AddRight(topRow, currentTag);
            }
            else
            {
                var arrow = Muted("›", 16, 0);
                AddRight(topRow, arrow);
            }
            body.Children.Add(topRow);

            var title = Title(name, 16);
            title.Foreground = Brush(active ? "AccentText" : "TextMain");
            body.Children.Add(title);
            body.Children.Add(StudioText(phase, 12.5, 4));

            var tile = ActionButton(body, go, false);
            tile.Style = Style("CardButton");
            tile.Tag = active ? "Active" : null;
            tile.Margin = new Thickness(0, 0, 10, 10);
            tile.MinHeight = 92;
            AutomationProperties.SetName(tile, name);
            return tile;
        }

        private void DrawProjectBoard(Border header, WrapPanel commands, UniformGrid steps, Expander later, Border summary, int current)
        {
            var card = StudioSection(null, "项目档案");
            header.Background = Brush("SelectedNavigation");
            header.BorderBrush = Brush("Focus");
            card.Children.Add(header);
            card.Children.Add(summary);
            var actionTitle = Muted("快捷操作", 12.5, 16);
            actionTitle.Margin = new Thickness(0, 16, 0, 6);
            card.Children.Add(actionTitle);
            card.Children.Add(commands);

            var pipeline = StudioSection(null, "阶段路线图");
            pipeline.Children.Add(steps);
            pipeline.Children.Add(later);
            content.Children.Add(StudioColumns(card, pipeline, 1, 1.32, 820));
            UiFx.StaggerEntrance(new UIElement[] { header, steps, summary }, Sys);
        }

        private FrameworkElement AssetMetrics(int drafted, int review, int approved, int total)
        {
            var metrics = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(0, 8, 0, 18) };
            var labels = new[] { "提示词就绪", "待人工审图", "已通过" };
            var values = new[] { drafted + " / " + total, review.ToString(), approved.ToString() };
            for (int index = 0; index < labels.Length; index++)
            {
                var body = new DockPanel { Margin = new Thickness(18, 14, 18, 14), LastChildFill = true };
                var val = Title(values[index], 24);
                val.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
                if (index == 1 && review > 0) val.Foreground = Brush("Review");
                else if (index == 2 && approved > 0) val.Foreground = Brush("AccentText");
                val.VerticalAlignment = VerticalAlignment.Center;
                AddRight(body, val);
                var label = Muted(labels[index], 13, 0);
                label.VerticalAlignment = VerticalAlignment.Center;
                body.Children.Add(label);

                var card = Panel(body);
                if (index == 1 && review > 0)
                {
                    card.Background = Brush("SelectedNavigation");
                    card.BorderBrush = Brush("Review");
                }
                card.Margin = new Thickness(0, 0, index == 2 ? 0 : 12, 0);
                metrics.Children.Add(card);
            }
            return metrics;
        }
    }
}
