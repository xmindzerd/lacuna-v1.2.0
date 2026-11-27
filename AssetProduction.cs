using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OneClickFilm
{
    public sealed class AssetWork
    {
        internal string SavedSnapshot;
        public string AnalysisId { get; set; }
        public string Style { get; set; }
        public string ReferencePath { get; set; }
        public string ReferenceDigest { get; set; }
        public List<AssetItem> Items { get; set; }
        public List<string> PendingPromptIds { get; set; }
        public List<string> PromptRequestIds { get; set; }
        public string PromptRequestStyle { get; set; }
        public string PromptRequestSkill { get; set; }
        public string DeliveredId { get; set; }
        public List<AssetGroup> Groups { get; set; }
        public Dictionary<string, AssetAttempt> BaseImages { get; set; }
        public Dictionary<string, List<AssetAttempt>> BaseHistory { get; set; }
        public AssetAutoReviewSummary LastAutoReview { get; set; }
        public AssetImageBatch ImageBatch { get; set; }
        public Dictionary<string, AssetImageBatch> OtherImageBatches { get; set; }

        internal IEnumerable<KeyValuePair<string, AssetImageBatch>> Batches()
        {
            if (ImageBatch != null) yield return new KeyValuePair<string, AssetImageBatch>("出图", ImageBatch);
            if (OtherImageBatches != null) foreach (var pair in OtherImageBatches) yield return pair;
        }
        internal AssetImageBatch Batch(string name)
        {
            if (name == "出图") return ImageBatch;
            AssetImageBatch batch;
            return OtherImageBatches != null && OtherImageBatches.TryGetValue(name, out batch) ? batch : null;
        }
        internal void StoreBatch(string name, AssetImageBatch batch)
        {
            if (name == "出图") { ImageBatch = batch; return; }
            if (OtherImageBatches == null) OtherImageBatches = new Dictionary<string, AssetImageBatch>();
            if (batch == null) OtherImageBatches.Remove(name); else OtherImageBatches[name] = batch;
        }
    }

    public sealed class AssetItem
    {
        public string Id { get; set; }
        public AnalysisAsset Source { get; set; }
        public string Prompt { get; set; }
        public List<int> PromptLines { get; set; }
        public string Assumptions { get; set; }
        public SkillReference PromptSkill { get; set; }
        public string PromptOrigin { get; set; }
        public string PromptDocument { get; set; }
        public string PromptStyle { get; set; }
        public string Conflict { get; set; }
        public AssetPromptRepair PendingRepair { get; set; }
        public string PromptRepairNote { get; set; }
        public string ActiveId { get; set; }
        public List<AssetAttempt> Attempts { get; set; }
        internal AssetAttempt Active { get { return Attempts.LastOrDefault(a => a.Id == ActiveId); } }
    }

    public sealed class AssetAttempt
    {
        public bool TimedOut { get; set; }
        public string Id { get; set; }
        public string State { get; set; }
        public string Prompt { get; set; }
        public List<int> PromptLines { get; set; }
        public string Style { get; set; }
        public string Channel { get; set; }
        public string PlatformId { get; set; }
        public string ReferencePath { get; set; }
        public string ReferenceDigest { get; set; }
        public string Detail { get; set; }
        public string BaseImagePath { get; set; }
        public SkillReference ImageSkill { get; set; }
        public string TargetSize { get; set; }
        public long DurationMs { get; set; }
        public string BaseRevision { get; set; }
        public SkillReference PromptSkill { get; set; }
        public string BaseImageDigest { get; set; }
        public string Path { get; set; }
        public string Digest { get; set; }
        public string Format { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string CreatedUtc { get; set; }
        public string VisualCheck { get; set; }
        public string ReviewedUtc { get; set; }
        public string ReviewNote { get; set; }
    }

    public sealed class AssetDelivery
    {
        public string Id { get; set; }
        public string AnalysisId { get; set; }
        public string ConfirmedUtc { get; set; }
        public List<AssetItem> Items { get; set; }
    }

    public sealed class AssetDraft
    {
        public string id { get; set; }
        public string prompt { get; set; }
        public string assumptions { get; set; }
        public string document { get; set; }
        public string conflict { get; set; }
        public List<int> lines { get; set; }
    }

    public sealed class AssetDrafts
    {
        public List<AssetDraft> items { get; set; }
    }

    internal static partial class AssetProduction
    {
        internal static string Folder(string project, string analysis)
        {
            if (!AgentData.ValidId(analysis)) throw new InvalidDataException("资产所绑定的分析版本无效。");
            return AgentData.Inside(project, Path.Combine("资产制作", analysis));
        }

        // Framework JavaScriptSerializer enumerates the runtime's weakly cached PropertyInfo list.
        // Retain it across loads so unchanged source facts keep their original serialized order.
        private static readonly System.Reflection.PropertyInfo[] SourceProperties = typeof(AnalysisAsset).GetProperties();

        internal static AssetWork Load(string project, AnalysisVersion version, AnalysisResult result)
        {
            GC.KeepAlive(SourceProperties);
            var work = AgentData.Read<AssetWork>(Path.Combine(Folder(project, version.Id), "制作记录.json"));
            if (work == null)
                work = new AssetWork { AnalysisId = version.Id, Items = result.Assets.Select((asset, i) => new AssetItem
                {
                    Id = (i + 1).ToString("D4", CultureInfo.InvariantCulture),
                    Source = asset, Attempts = new List<AssetAttempt>()
                }).ToList() };
            if (work.AnalysisId != version.Id || work.Items == null || work.Items.Count != result.Assets.Count ||
                work.Items.Any(item => item == null || item.Attempts == null || item.Source == null))
                throw new InvalidDataException("资产制作记录与已确认的清单不一致。");
            for (int i = 0; i < work.Items.Count; i++)
            {
                var expected = result.Assets[i];
                var item = work.Items[i];
                var itemEvidence = item.Source.evidence ?? new List<int>();
                var expectedEvidence = expected.evidence ?? new List<int>();
                if (item.Id != (i + 1).ToString("D4", CultureInfo.InvariantCulture) ||
                    item.Source.name != expected.name || item.Source.category != expected.category ||
                    item.Source.reason != expected.reason || item.Source.identity != expected.identity ||
                    item.Source.variant != expected.variant || item.Source.direction != expected.direction || !itemEvidence.SequenceEqual(expectedEvidence))
                    throw new InvalidDataException("资产制作记录中的条目与已确认清单不一致。");
            }
            // Both the gallery and the batch serialize the same attempt. Re-link after loading
            // so manual review and late recovery update the same record.
            foreach (var pair in work.Batches())
                foreach (var job in pair.Value.Jobs.Where(j => !j.IsBase && j.Attempt != null))
                {
                    var item = work.Items.FirstOrDefault(i => i.Id == job.ItemId);
                    var stored = item == null ? null : item.Attempts.Find(a => a.Id == job.Attempt.Id);
                    if (stored != null && (stored.Path != null || job.Attempt.Path == null)) job.Attempt = stored;
                }
            work.SavedSnapshot = AgentData.Json().Serialize(work);
            if (result.VisualSettings != null)
                for (int i = 0; i < work.Items.Count; i++) work.Items[i].Source = result.Assets[i];
            RestoreBaseHistory(work);
            if (NeedsDirectSpecs(work) && UsesDirectSpecs(project))
                PopulateDirectImageSpecs(project, work, result);
            return work;
        }

        internal static void RequireCurrent(string project, string analysisId)
        {
            var source = AnalysisData.CurrentScript(project);
            var confirmed = AnalysisData.Load(project).Confirmed(source);
            if (confirmed == null || confirmed.Id != analysisId)
                throw new InvalidOperationException("剧本或已确认的资产清单已变化。请重新打开资产制作页。");
        }

        internal static void Save(string project, AssetWork work)
        {
            SaveAssetFile(Path.Combine(Folder(project, work.AnalysisId), "制作记录.json"), work);
        }

        internal const string DirectPromptOrigin = "视觉设定直出（Codex 自动整理）";

        internal static bool UsesDirectSpecs(string project)
        {
            if (!FlowCreation.IsFlowProject(project)) return false;
            var config = FlowCreation.Config(project);
            return config != null && config.IsV2Builtin;
        }

        private static bool NeedsDirectSpecs(AssetWork work)
        {
            return work.PendingPromptIds != null || work.Items.Any(i => !(i.PromptOrigin ?? "").Contains("人工") &&
                (!PromptReady(i) || i.PromptOrigin == "待自动整理"));
        }

        internal static string CleanVisualStyle(string raw)
        {
            string style = Regex.Replace(raw ?? "", @"[（(][^（）()]*(?:成片固定|资产图固定|画幅|宽高比)[^（）()]*[）)]", "");
            style = Regex.Replace(style, @"(?:成片固定|资产图固定)[^。；;\r\n]*[。；;]?", "");
            style = Regex.Replace(style, @"[^。；;\r\n]*(?:保留中英(?:逐行)?对应|仅建本段可拍资产|只在后期叠加[，,、]?不烘焙进资产图)[^。；;\r\n]*[。；;]?", "");
            style = Regex.Replace(style, @"(?:画幅|宽高比|横屏|竖屏|横版|竖版|\d+\s*[:：]\s*\d+)", "");
            style = Regex.Replace(style, @"[（(][\s，,、·/|｜；;：:。]*[）)]", "");
            style = Regex.Replace(style, @"([，,、；;。])(?:\s*[，,、；;。])+", "$1");
            return style.Trim().Trim(' ', '，', ',', '·', '/', '、', '｜', '|', '；', ';', '：', ':', '。').Trim();
        }

        internal static string BuildTextPolicyRule(string category, string propTextPolicy)
        {
            if (category != "道具" || String.IsNullOrWhiteSpace(propTextPolicy))
                return "文字规则：整幅画面严禁出现任何文字、标牌字母、数字、标签、水印、字幕与画框。";
            string policy = propTextPolicy.ToLowerInvariant();
            if (policy.Contains("待创作者拍板") || policy.Contains("待拍板") || policy.Contains("待定"))
                return "文字规则：道具字句待创作者拍板，本轮严禁杜撰文案；道具表面与整幅画面暂不出现任何文字、字母、数字、标签、水印、字幕与画框。";
            if ((policy.Contains("可读") || Regex.IsMatch(policy, @"\breadable\b")) &&
                !policy.Contains("不可读") && !policy.Contains("非可读") && !policy.Contains("无字"))
                return "文字规则：按道具设定在指定表面清晰呈现可读字迹，笔画工整可辨；仅严禁画面水印、字幕、UI 标注、画框及无关文字。";
            if (policy.Contains("轮廓") || policy.Contains("symbolic") || policy.Contains("不可读"))
                return "文字规则：道具表面仅保留不可读的印刷/书写版式轮廓与质感纹理；严禁真实可读文字、水印、字幕与画框。";
            if (policy.Contains("后期叠加") || policy.Contains("留白") || policy.Contains("postproduction"))
                return "文字规则：在道具指定表面保留干净、无遮挡区域供后期叠加文字；严禁画面文字、水印、字幕与画框。";
            return "文字规则：道具表面与整幅画面严禁出现任何文字、字母、数字、标签、水印、字幕与画框。";
        }

        private static string ImageLayout(string category)
        {
            if (category == "场景")
                return "版式规范：横屏 4:3 连续单幅场景空镜参考图，画面铺满整张画布、边缘齐平，严禁上下或四周出现黑边、灰边、画框或分格拼图。空镜无人（不出现任何人物），前景、中景、背景空间层次与门窗动线清楚，光线方向、色温与环境质感统一。";
            if (category == "道具")
                return "版式规范：横屏 4:3 道具设定参考图，画面铺满整张画布、边缘齐平，严禁上下或四周出现黑边、灰边或画框。纯干净浅灰无缝影棚背景、柔和均匀棚光，清晰呈现器物比例、结构部件、材质工艺与当前状态；独立立体器物采用正面、严格 90 度正侧面、背面三视图水平对齐排布，不互相遮挡。";
            return "版式规范：横屏 4:3 角色设定五视图，画面铺满整张画布、边缘齐平，严禁上下或四周出现黑边、灰边、画框或留白条。纯干净浅灰无缝影棚背景，柔和均匀平光。左侧上下叠放两张头部特写：上方正脸平视头肩特写，下方 45 度四分之三侧脸头肩特写（保留完整头顶与下颌）；右侧从左到右等高水平排布三张完整全身立绘：正面全身、严格 90 度正侧面全身、背面全身，从头顶到鞋底完整入画、目光与头脚高度一致、彼此清晰分开不重叠。单一连贯造型，不混入互斥状态。";
        }

        internal static void PopulateDirectImageSpecs(string project, AssetWork work, AnalysisResult analysis)
        {
            var visual = analysis.VisualSettings;
            if (String.IsNullOrWhiteSpace(work.Style) && visual != null)
                work.Style = CleanVisualStyle(visual.ProductionFormat);
            string style = CleanVisualStyle(work.Style);
            foreach (var item in work.Items)
            {
                bool queued = work.PendingPromptIds != null && work.PendingPromptIds.Contains(item.Id);
                if ((item.PromptOrigin ?? "").Contains("人工") || (PromptReady(item) && !queued && item.PromptOrigin != "待自动整理")) continue;
                var a = item.Source;
                var parts = new List<string> { a.category + "资产 · " + a.name };
                Action<string,string> add = (label, value) => {
                    if (!String.IsNullOrWhiteSpace(value)) parts.Add(label + "：" + value.Trim());
                };
                parts.Add(ImageLayout(a.category));
                add("本体", a.identity);
                add("提示词名称", a.promptName);
                add("造型/状态", a.variant);
                add("外观特征", a.appearance);
                if (!String.IsNullOrWhiteSpace(a.episodeState) && !(a.appearance ?? "").Contains(a.episodeState.Trim()))
                    add("当前阶段状态", a.episodeState);
                add("时代背景与器物制式", !String.IsNullOrWhiteSpace(a.eraBackground) ? a.eraBackground : visual == null ? null : visual.EraBackground);
                if (String.IsNullOrWhiteSpace(a.appearance))
                {
                    string legacy = a.direction ?? "";
                    if (!String.IsNullOrWhiteSpace(a.voiceDirection)) legacy = legacy.Replace(a.voiceDirection, "");
                    add("视觉交接", Regex.Replace(legacy, @"(?m)^[ \t]*角色声线[：:].*(?:\n|$)", ""));
                }
                if (a.continuityLocks != null)
                    add("原样保持的连戏锁", String.Join("；", a.continuityLocks.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct()));
                if (a.category != "场景" && a.category != "道具")
                    parts.Add("还原约束：真实还原年龄、骨相、肤质、体型、发型与服饰细节，严禁用泛化网红磨皮审美覆盖设定。");
                if (!a.isBaseState && !String.IsNullOrWhiteSpace(a.parentAssetKey))
                    parts.Add("状态派生约束：严格继承基准成图的固定外观、服装款式或空间与器物固定结构，仅叠加本项指定的状态变化，严禁换装或改动固定版型。");
                add("画面风格", style);
                if (a.category == "道具") add("道具字迹设定", a.propTextPolicy);
                parts.Add(BuildTextPolicyRule(a.category, a.propTextPolicy));
                item.Prompt = String.Join("\n", parts);
                item.PromptLines = (a.evidence ?? new List<int>()).Where(n => n > 0).Distinct().ToList();
                if (item.PromptLines.Count == 0)
                {
                    var scenes = (analysis.Parts ?? new List<AnalysisPart>()).SelectMany(p => p.scenes ?? new List<AnalysisScene>())
                        .Where(s => s.kind == "story" || s.kind == "vo_transition").ToList();
                    string identity = (a.identity ?? "").Trim();
                    item.PromptLines = (analysis.Assets ?? new List<AnalysisAsset>()).Where(other => identity.Length > 0 &&
                        other.category == a.category && String.Equals(other.identity, identity, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(other => other.evidence ?? new List<int>()).Where(n => n > 0 && scenes.Any(s => s.first <= n && n <= s.last))
                        .Distinct().OrderBy(n => n).ToList();
                    string strategy = "沿用同类同本体的可拍行号";
                    if (item.PromptLines.Count == 0 && scenes.Count > 0 && identity.Length > 0)
                    {
                        var script = AnalysisData.CurrentScript(project);
                        if (script != null)
                            item.PromptLines = scenes.SelectMany(s => Enumerable.Range(s.first, s.last - s.first + 1))
                                .Where(n => n > 0 && n <= script.Lines.Length && script.Lines[n-1].IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0)
                                .Distinct().OrderBy(n => n).ToList();
                        strategy = "引用可拍场次中明确出现本体名称的原文行";
                    }
                    string error = a.name + " 的视觉设定缺少可追溯的剧本行。";
                    FlowIncidentStore.Record(project, new FlowIncident { Half = FlowHalf.PreAssets, StageIndex = FlowTelemetry.StageAssets,
                        StageTitle = "资产准备", Category = FlowIncidentCategory.AssetPreparationAndSchema, SubStep = a.name,
                        RawErrorMessage = error, Summary = error, AutoRepairStrategy = strategy,
                        ContextSnapshot = DirectorPipeline.FormatLineRanges(item.PromptLines), Resolved = item.PromptLines.Count > 0,
                        PermanentFixSuggestion = "在视觉设定导出与导入时核验每个资产的原文依据。" });
                    if (item.PromptLines.Count == 0) throw FlowIncidentStore.MarkRecorded(new InvalidDataException(error), FlowTelemetry.StageAssets);
                }
                item.PromptOrigin = DirectPromptOrigin;
                item.PromptStyle = work.Style;
                item.PromptSkill = null;
                item.PromptDocument = item.Assumptions = item.Conflict = item.PromptRepairNote = null;
                item.PendingRepair = null;
            }
            work.PendingPromptIds = work.PromptRequestIds = null;
            work.PromptRequestStyle = work.PromptRequestSkill = null;
            Save(project, work);
        }

        internal static void ApproveAndDeliver(string project, AssetWork work, Dictionary<string, string> displayed)
        {
            if (work.Items.Count == 0 || work.Items.Count != displayed.Count)
                throw new InvalidOperationException("资产清单已经变化，请刷新后确认。");
            foreach (var item in work.Items)
            {
                string id;
                if (item.Active == null || !displayed.TryGetValue(item.Id, out id) || item.Active.Id != id ||
                    (item.Active.State != "待审图" && item.Active.State != "已通过"))
                    throw new InvalidOperationException(item.Source.name + " 的图片已改变或尚未就绪，请刷新后确认。");
                Verify(project, item.Active);
            }
            if (work.DeliveredId != null && work.Items.All(i => i.Active.State == "已通过"))
            {
                var previous = ReadDeliveryRecord(project, work);
                if (previous != null && previous.Items.All(i => displayed[i.Id] == i.ActiveId)) return;
            }
            foreach (var item in work.Items.Where(i => i.Active.State == "待审图"))
            {
                item.Active.State = "已通过";
                item.Active.ReviewedUtc = AnalysisData.Now();
                item.Active.ReviewNote = "整套资产一键通过";
            }
            Deliver(project, work);
        }

        internal static AssetDelivery Deliver(string project, AssetWork work)
        {
            RequireCurrent(project, work.AnalysisId);
            foreach (var item in work.Items)
            {
                var attempt = item.Active;
                if (attempt == null || attempt.State != "已通过")
                    throw new InvalidOperationException("尚未通过人工审图：" + item.Source.name);
                Verify(project, attempt);
            }
            var delivery = new AssetDelivery { Id = AgentData.Id(), AnalysisId = work.AnalysisId,
                ConfirmedUtc = AnalysisData.Now(), Items = work.Items.Select(i => new AssetItem
                {
                    Id = i.Id, Source = i.Source, ActiveId = i.ActiveId, Prompt = i.Active.Prompt,
                    PromptSkill = i.Active.PromptSkill, PromptLines = i.Active.PromptLines, PromptStyle = i.Active.Style,
                    Attempts = new List<AssetAttempt> { i.Active }
                }).ToList() };
            AgentData.Save(DeliveryPath(project, work.AnalysisId, delivery.Id), delivery);
            work.DeliveredId = delivery.Id;
            Save(project, work);
            return delivery;
        }

        private static string DeliveryPath(string project, string analysisId, string id)
        {
            if (!AgentData.ValidId(id)) throw new InvalidDataException("无效的资产交付编号。");
            return AgentData.Inside(Folder(project, analysisId), Path.Combine("已确认版本", id + ".json"));
        }

        internal static AssetDelivery ReadDelivery(string project, AssetWork work)
        {
            var result=ReadDeliveryRecord(project,work);
            if(result!=null) foreach(var item in result.Items)
            {
                try { Verify(project,item.Active); }
                catch(Exception e) { throw new InvalidDataException(item.Source.name+" · "+e.Message,e); }
            }
            return result;
        }

        internal static AssetDelivery ReadDeliveryRecord(string project, AssetWork work)
        {
            if (String.IsNullOrEmpty(work.DeliveredId)) return null;
            var result = AgentData.Read<AssetDelivery>(DeliveryPath(project, work.AnalysisId, work.DeliveredId));
            if (result == null || result.AnalysisId != work.AnalysisId || result.Id != work.DeliveredId ||
                result.Items == null || result.Items.Count != work.Items.Count)
                throw new InvalidDataException("已确认资产版本丢失或记录不完整。");
            for (int i = 0; i < result.Items.Count; i++)
            {
                var item = result.Items[i];
                if (item.Id != work.Items[i].Id || item.Source.name != work.Items[i].Source.name ||
                    item.Active == null || item.Active.State != "已通过")
                    throw new InvalidDataException("已确认资产项与清单不符。");
            }
            return result;
        }

        internal static void Inspect(string path, AssetAttempt attempt)
        {
            AgentMedia.ValidateOutput(path, "图片");
            using (var file = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || (long)frame.PixelWidth * frame.PixelHeight > 64000000)
                    throw new InvalidDataException("图片尺寸无效或超出可预览范围。");
                // Pull every decoded row to catch errors that header-only decoding would miss.
                var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] row = new byte[checked(frame.PixelWidth * 4)];
                for (int y = 0; y < frame.PixelHeight; y++)
                    pixels.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, 1), row, row.Length, 0);
                attempt.Width = frame.PixelWidth;
                attempt.Height = frame.PixelHeight;
                attempt.Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            }
            attempt.Digest = SystemData.Digest(path);
        }

        internal static string Verify(string project, AssetAttempt attempt)
        {
            var path = AgentData.Inside(project, attempt.Path);
            if (!File.Exists(path)) throw new InvalidDataException("资产图缺失：" + attempt.Path);
            if (!String.Equals(SystemData.Digest(path), attempt.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资产图已被替换或损坏：" + attempt.Path);
            return path;
        }

        internal static void Attach(string project, AssetWork work, AssetAttempt attempt, string source)
        {
            string folder = Folder(project, work.AnalysisId);
            if (!AgentData.ValidId(attempt.Id)) throw new InvalidDataException("无效的资产尝试编号。");
            bool fit = false;
            if (ImageOutputSettings.Ratio(attempt.TargetSize) == ImageOutputSettings.DefaultAssetRatio)
            {
                var original = new AssetAttempt();
                Inspect(source, original);
                attempt.SourceWidth = original.Width;
                attempt.SourceHeight = original.Height;
                fit = (long)original.Width * 3 != (long)original.Height * 4;
            }
            string extension = fit ? ".png" : Path.GetExtension(source).ToLowerInvariant();
            string destination = AgentData.Inside(folder, Path.Combine("图片", attempt.Id + extension));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (!File.Exists(destination))
            {
                string temporary = destination + "." + AgentData.Id() + extension;
                try
                {
                    if (fit) ImageOutputSettings.SaveFourThree(source, temporary, attempt.TargetSize);
                    else File.Copy(source, temporary, false);
                    Inspect(temporary, attempt);
                    File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else Inspect(destination, attempt);
            attempt.Path = destination.Substring(Path.GetFullPath(project).TrimEnd('\\', '/').Length + 1);
            attempt.State = "待审图";
            attempt.VisualCheck = "未验证 · 可单独运行视觉自检";
            attempt.Detail = "";
        }

        internal static void SetReference(string project, AssetWork work, string source)
        {
            var info = new AssetAttempt();
            Inspect(source, info);
            string relative = Path.Combine("资产制作", work.AnalysisId, "参考图",
                AgentData.Id() + Path.GetExtension(source).ToLowerInvariant());
            string file = AgentData.Inside(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.Copy(source, file, false);
            if (!String.Equals(info.Digest, SystemData.Digest(file), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("参考图在复制时发生变化，请重新选择。");
            work.ReferencePath = relative;
            work.ReferenceDigest = info.Digest;
        }

        internal static string Reference(string project, AssetWork work)
        {
            if (String.IsNullOrEmpty(work.ReferencePath)) return null;
            string file = AgentData.Inside(project, work.ReferencePath);
            if (!File.Exists(file) || !String.Equals(SystemData.Digest(file), work.ReferenceDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资产参考图缺失或已被替换，请重新选择参考图。");
            return file;
        }

        internal static void Recover(string project, AssetWork work, AssetAttempt attempt)
        {
            var job = new AgentJob { Id = attempt.Id };
            var folder = AgentMedia.JobFolder(project, job);
            var receipt = AgentMedia.ReadJobReceipt(folder, null);
            if (receipt != null && receipt.State == "已取回")
            {
                Attach(project, work, attempt, AgentData.Inside(folder, receipt.Output));
                return;
            }
            if (FlowChannels.IsNative(attempt.PlatformId) && TryRecoverImage(project, work, attempt)) return;
            bool submitted = File.Exists(Path.Combine(folder, "invoked"));
            attempt.State = submitted ? "需核对" : "未提交";
            if (String.IsNullOrEmpty(attempt.Detail))
                attempt.Detail = submitted ? "未找到可归属的图片，请到原渠道核对。" : "没有提交记录，可以重新出图。";
        }

        internal static string CodexText(string exe, string model, string prompt, CancellationToken cancel, string picture = null, SelectedSkill skill = null)
        {
            string folder = TempFolder();
            try
            {
                return AgentRunner.Run(exe, folder, prompt, model, null, null, null,
                    picture == null ? null : new List<string> { picture },
                    null, null, false, null, cancel, skill: skill).Answer;
            }
            finally { RemoveTemp(folder); }
        }

        private static string TempFolder()
        {
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "一键成片-资产-" + AgentData.Id())).FullName;
        }

        private static void RemoveTemp(string folder)
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
