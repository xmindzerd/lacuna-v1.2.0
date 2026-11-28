using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal static class ImageOutputSettings
    {
        internal const string SkillOperation = "asset.image.generate";
        internal const int ImageConcurrency = 6;
        internal static int TimeoutMinutes(int configured)
        {
            return configured >= 2 && configured <= 30 ? configured : 6;
        }
        internal const int ReturnGraceMs = 120000;
        internal const int MaxRetryCount = 1;

        internal const string DefaultAssetRatio = "4:3";
        private static readonly string[] BuiltinRatios = { DefaultAssetRatio, "1:1", "3:2", "2:3", "16:9", "9:16" };
        private static readonly int[] BuiltinLongEdges = { 1024, 1536, 2048 };

        private static string ForRatio(string ratio, int pixels)
        {
            var sides = ratio.Split(':').Select(Int32.Parse).ToArray();
            int multiple = pixels / Math.Max(sides[0], sides[1]);
            return (sides[0] * multiple) + "x" + (sides[1] * multiple);
        }
        internal static IEnumerable<string> Sizes(AssetChannel channel)
        {
            if (channel != null && ImageApiProfiles.IsProfile(channel.Id))
                return new[] { "1536x1152", "1024x1024", "1536x1024", "1024x1536" };
            if (channel == null || FlowChannels.IsResponsesApi(channel)) return BuiltinRatios.SelectMany(ratio => BuiltinLongEdges.Select(pixels => ForRatio(ratio, pixels)));
            if (channel.Driver == FlowChannels.Dreamina) return new[] { "2048x1536", "2048x1152", "1152x2048" };
            string args = channel.Arguments ?? "";
            if (!args.Contains("{width}") || !args.Contains("{height}")) return new string[0];
            return (channel.OutputSizes ?? "").Split(new[] { '\r', '\n', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant())
                .Where(IsValid).Distinct();
        }

        // The reference sheet has its own canvas, independent of the film orientation.
        internal static string DefaultSize(AssetChannel channel)
        {
            return Sizes(channel).Where(s => Ratio(s) == DefaultAssetRatio)
                .OrderBy(s => Math.Abs(1536 - Int32.Parse(s.Split('x')[0]))).FirstOrDefault() ?? "1536x1152";
        }

        internal static string ProviderSize(AssetChannel channel, string target)
        {
            return Sizes(channel).Contains(target) ? target : null;
        }

        // Keep the whole subject when a provider supplies a different canvas.
        // The provider's original remains in its job folder for recovery.
        internal static void SaveFourThree(string source, string output, string target)
        {
            var sides = target.Split('x').Select(Int32.Parse).ToArray();
            int width = sides[0], height = sides[1], stride = checked(width * 4);
            using (var stream = File.OpenRead(source))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                double scale = Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);
                var fitted = new FormatConvertedBitmap(new TransformedBitmap(frame, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
                var pixels = new byte[checked(stride * height)];
                var corner = new byte[4];
                var mean = new int[4];
                foreach (int x in new[] { 0, fitted.PixelWidth - 1 })
                    foreach (int y in new[] { 0, fitted.PixelHeight - 1 })
                    {
                        fitted.CopyPixels(new Int32Rect(x,y,1,1), corner, 4, 0);
                        for (int c = 0; c < 4; c++) mean[c] += corner[c];
                    }
                for (int i = 0; i < stride; i++) pixels[i] = (byte)(mean[i % 4] / 4);
                for (int y = 1; y < height; y++) Buffer.BlockCopy(pixels, 0, pixels, y * stride, stride);
                int copyW = Math.Min(width, fitted.PixelWidth);
                int copyH = Math.Min(height, fitted.PixelHeight);
                int offsetX = Math.Max(0, (width - copyW) / 2);
                int offsetY = Math.Max(0, (height - copyH) / 2);
                int offset = offsetY * stride + offsetX * 4;
                fitted.CopyPixels(new Int32Rect(0, 0, copyW, copyH), pixels, stride, offset);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride)));
                using (var result = File.Create(output)) encoder.Save(result);
            }
        }

        internal static bool IsValid(string size)
        {
            if (size == null || !Regex.IsMatch(size, @"^[1-9][0-9]{1,3}x[1-9][0-9]{1,3}$")) return false;
            var sides = size.Split('x').Select(Int32.Parse).ToArray();
            return (long)sides[0] * sides[1] <= 64000000;
        }

        internal static string Ratio(string size)
        {
            if (!IsValid(size)) return "";
            var sides = size.Split('x').Select(Int32.Parse).ToArray();
            int a = sides[0], b = sides[1];
            while (b != 0) { int remainder = a % b; a = b; b = remainder; }
            return sides[0] / a + ":" + sides[1] / a;
        }

        internal static string Label(string size)
        {
            return !IsValid(size) ? "自动 · 保留原图" : Ratio(size) + " · " + size.Replace("x", " × ");
        }
        internal static string ApplySize(string prompt, string size)
        {
            if (!IsValid(size)) return prompt;
            return prompt + "\n本次出图目标规格为 " + Label(size) + "。请依该比例构图。";
        }

        internal static string SizeStatus(AssetAttempt attempt)
        {
            if (!IsValid(attempt.TargetSize)) return "";
            if (attempt.SourceWidth > 0 && (long)attempt.SourceWidth * 3 != (long)attempt.SourceHeight * 4 && Ratio(attempt.TargetSize) == DefaultAssetRatio)
                return "  ·  横屏 4:3 · 原图 " + attempt.SourceWidth + " × " + attempt.SourceHeight + "，已等比补边";
            return "  ·  目标 " + Label(attempt.TargetSize) +
                (attempt.TargetSize == attempt.Width + "x" + attempt.Height ? "（已匹配）" :
                    "（渠道回图尺寸有差异，已保留原图）");
        }
    }

    internal sealed partial class Workspace
    {
        private string assetTargetSize;

        private WrapPanel MakeImageSizes(AssetChannel channel, out Action<AssetChannel> bindChannel)
        {
            var panel = new WrapPanel();
            var ratio = new ComboBox { MinWidth = 116, Margin = new Thickness(0, 0, 8, 4) };
            var sizes = new ComboBox { MinWidth = 156, Margin = new Thickness(0, 0, 8, 4) };
            AutomationProperties.SetName(ratio, "画面比例");
            AutomationProperties.SetName(sizes, "图片分辨率");
            panel.Children.Add(ratio);
            panel.Children.Add(sizes);
            bool changing = false;
            string[] available = new string[0];
            Action setSize = delegate
            {
                string previous = assetTargetSize;
                string aspect = Convert.ToString(((ComboBoxItem)ratio.SelectedItem).Tag);
                sizes.Items.Clear();
                if (aspect.Length == 0)
                    sizes.Items.Add(new ComboBoxItem { Tag = "", Content = "分辨率 · 自动" });
                else
                    foreach (var s in available.Where(s => ImageOutputSettings.Ratio(s) == aspect)
                        .OrderBy(s => s.Split('x').Select(Int32.Parse).Max()))
                        sizes.Items.Add(new ComboBoxItem { Tag = s, Content = s.Replace("x", " × ") });
                var match = sizes.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == previous);
                if (match == null && ImageOutputSettings.IsValid(previous))
                {
                    int edge = previous.Split('x').Select(Int32.Parse).Max();
                    match = sizes.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ImageOutputSettings.IsValid((string)i.Tag) &&
                        Math.Abs(((string)i.Tag).Split('x').Select(Int32.Parse).Max() - edge) <= 2);
                }
                sizes.SelectedItem = match ?? sizes.Items[Math.Min(1, sizes.Items.Count - 1)];
                assetTargetSize = Convert.ToString(((ComboBoxItem)sizes.SelectedItem).Tag);
            };
            bindChannel = delegate(AssetChannel selected)
            {
                changing = true;
                string defaultSize = ImageOutputSettings.DefaultSize(selected);
                available = ImageOutputSettings.Sizes(selected).Concat(new[] { defaultSize }).Distinct().ToArray();
                ratio.Items.Clear();
                ratio.Items.Add(new ComboBoxItem { Tag = "", Content = "比例 · 自动" });
                foreach (string aspect in available.Select(ImageOutputSettings.Ratio).Distinct())
                    ratio.Items.Add(new ComboBoxItem { Tag = aspect, Content = aspect == ImageOutputSettings.DefaultAssetRatio ? "比例 · 4:3（默认）" : "比例 · " + aspect });
                ratio.SelectedItem = ratio.Items.Cast<ComboBoxItem>().FirstOrDefault(i =>
                    (string)i.Tag == ImageOutputSettings.Ratio(assetTargetSize ?? defaultSize)) ?? ratio.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == ImageOutputSettings.DefaultAssetRatio);
                setSize();
                changing = false;
            };
            ratio.SelectionChanged += delegate { if (!changing && ratio.SelectedItem != null) setSize(); };
            sizes.SelectionChanged += delegate
            {
                var selected = sizes.SelectedItem as ComboBoxItem;
                if (selected != null) assetTargetSize = Convert.ToString(selected.Tag);
            };
            bindChannel(channel);
            return panel;
        }
        private void DiscardPausedImages(string root, AnalysisVersion version, AnalysisResult analysis, string lane)
        {
            string answer;
            if (!Prompt("取消暂停的出图任务", "结束这次出图任务并移除恢复入口。已归档的图和提示词可继续查看。",
                "取消任务", "返回", false, out answer)) return;
            bool saved = UpdateAsset(root, version, analysis, work =>
            {
                var batch = work.Batch(lane);
                if (batch != null && batch.NeedsAction)
                {
                    foreach (var job in batch.Jobs.Where(j => !j.Done))
                        AssetProduction.ResetJob(work, job);
                    work.StoreBatch(lane, null);
                }
                else if (!work.Batches().Any()) foreach (var item in work.Items.Where(i => i.Active != null &&
                    (i.Active.State == "需核对" || i.Active.State == "运行中")))
                    AssetProduction.ResetJob(work, new AssetImageJob { ItemId = item.Id, Attempt = item.Active });
            }, lane);
            if (saved && SameProject(root, projectFolder))
                SetAssetProgress(root, "本次出图任务已取消，已归档的图片可以继续审图。", 0, 0, lane);
        }
    }
}