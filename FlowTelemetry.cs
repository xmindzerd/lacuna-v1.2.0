using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneClickFilm
{
    public sealed class FlowStageRecord
    {
        public int Index { get; set; }
        public string Key { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string State { get; set; }
        public string Summary { get; set; }
        public long ElapsedMs { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public int RepairedCount { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
    }

    public sealed class FlowRunTelemetryRecord
    {
        public string RunId { get; set; }
        public string OverallState { get; set; }
        public string CurrentHeadline { get; set; }
        public string CurrentDetail { get; set; }
        public int ActiveStageIndex { get; set; }
        public long TotalElapsedMs { get; set; }
        public string UpdatedUtc { get; set; }
        public List<FlowStageRecord> Stages { get; set; }
    }

    internal sealed class FlowWorkItem
    {
        internal string Key;
        internal int StageIndex;
        internal string StageTitle;
        internal string Title;
        internal string Detail;
        internal string State;
        internal int Done;
        internal int Total;
        internal DateTime StartedUtc;
        internal DateTime UpdatedUtc;
        internal long FinalElapsedMs;

        internal long LiveElapsedMs(DateTime nowUtc)
        {
            if (FinalElapsedMs > 0 || State == "已完成" || State == "已跳过" || State == "失败")
                return FinalElapsedMs;
            return Math.Max(0L, (long)(nowUtc - StartedUtc).TotalMilliseconds);
        }

        internal FlowWorkItem Clone(DateTime nowUtc)
        {
            return new FlowWorkItem
            {
                Key = Key,
                StageIndex = StageIndex,
                StageTitle = StageTitle,
                Title = Title,
                Detail = Detail,
                State = State,
                Done = Done,
                Total = Total,
                StartedUtc = StartedUtc,
                UpdatedUtc = UpdatedUtc,
                FinalElapsedMs = LiveElapsedMs(nowUtc)
            };
        }
    }

    internal sealed class FlowTimelineEntry
    {
        internal long Seq;
        internal string Time;
        internal int StageIndex;
        internal string Level;
        internal string Message;
    }

    internal sealed class FlowStageSnapshot
    {
        internal int Index;
        internal string Key;
        internal string Title;
        internal string Subtitle;
        internal string State;
        internal string Summary;
        internal long ElapsedMs;
        internal int Done;
        internal int Total;
        internal int RepairedCount;
        internal string StartedUtc;
        internal string FinishedUtc;
    }

    internal sealed class FlowLiveSnapshot
    {
        internal string ProjectRoot;
        internal string RunId;
        internal string OverallState;
        internal string CurrentHeadline;
        internal string CurrentDetail;
        internal int ActiveStageIndex;
        internal double OverallPercent;
        internal long TotalElapsedMs;
        internal long ActiveStageElapsedMs;
        internal bool IsRunning;
        internal FlowStageSnapshot[] Stages;
        internal FlowWorkItem[] ActiveItems;
        internal FlowTimelineEntry[] Timeline;
        internal int PreAssetIncidentCount;
        internal int PostAssetIncidentCount;
        internal long Revision;
        internal long IncidentRevision;
    }

    internal static class FlowTelemetry
    {
        internal const string TelemetryFile = @"一键成片\流程耗时.json";

        internal const int StageAnalysis = 0;
        internal const int StageAssets = 1;
        internal const int StageAssetReview = 2;
        internal const int StageStoryboard = 3;
        internal const int StagePrompts = 4;
        internal const int StagePromptReview = 5;

        internal const string StatePending = "待开始";
        internal const string StateRunning = "进行中";
        internal const string StateWaiting = "待确认";
        internal const string StateCompleted = "已完成";
        internal const string StateSkipped = "已跳过";
        internal const string StateWarning = "已自愈";
        internal const string StateFailed = "需关注";

        private static readonly string[] StageKeys =
        {
            "analysis", "assets", "asset-review", "storyboard", "prompts", "prompt-review"
        };

        private static readonly string[] StageTitles =
        {
            "剧本分析", "资产出图", "资产确认", "分镜与分段", "最终提示词", "提示词确认与交付"
        };

        private static readonly string[] StageSubtitles =
        {
            "1A 导演分析 · 1B 视觉设定",
            "4:3 锁脸 → 基准态 → 状态变体",
            "人工审核资产与下载前半段报告",
            "03 竖屏分镜 · 04 智能分段",
            "Stage 2 全局 · Stage 3 画布",
            "监看确认、导出交接与视频生成"
        };

        private sealed class StageBucket
        {
            internal int Index;
            internal string Key;
            internal string Title;
            internal string Subtitle;
            internal string State;
            internal string Summary;
            internal long AccumulatedMs;
            internal DateTime? SegmentStartedUtc;
            internal string StartedUtc;
            internal string FinishedUtc;
            internal int Done;
            internal int Total;
            internal int RepairedCount;

            internal long LiveMs(DateTime nowUtc)
            {
                long ms = AccumulatedMs;
                if (SegmentStartedUtc.HasValue)
                    ms += Math.Max(0L, (long)(nowUtc - SegmentStartedUtc.Value).TotalMilliseconds);
                return ms;
            }
        }

        private sealed class ProjectTelemetry
        {
            internal string Root;
            internal string RunId;
            internal string OverallState;
            internal string CurrentHeadline;
            internal string CurrentDetail;
            internal int ActiveStageIndex;
            internal bool IsRunning;
            internal long AccumulatedRunMs;
            internal DateTime? RunSegmentStartedUtc;
            internal StageBucket[] Stages;
            internal readonly Dictionary<string, FlowWorkItem> WorkItems = new Dictionary<string, FlowWorkItem>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<FlowTimelineEntry> Timeline = new List<FlowTimelineEntry>();
            internal long TimelineSeq;
            internal long Revision;
            internal long IncidentRevision;
            internal int PreAssetIncidentCount;
            internal int PostAssetIncidentCount;

            internal long LiveTotalMs(DateTime nowUtc)
            {
                long ms = AccumulatedRunMs;
                if (RunSegmentStartedUtc.HasValue)
                    ms += Math.Max(0L, (long)(nowUtc - RunSegmentStartedUtc.Value).TotalMilliseconds);
                return ms;
            }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ProjectTelemetry> Projects =
            new Dictionary<string, ProjectTelemetry>(StringComparer.OrdinalIgnoreCase);

        private static string NormalizeRoot(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return "";
            try { return Path.GetFullPath(root).TrimEnd('\\', '/'); }
            catch (Exception) { return root.Trim().TrimEnd('\\', '/'); }
        }

        private static StageBucket[] FreshStages()
        {
            var result = new StageBucket[StageKeys.Length];
            for (int i = 0; i < StageKeys.Length; i++)
            {
                result[i] = new StageBucket
                {
                    Index = i,
                    Key = StageKeys[i],
                    Title = StageTitles[i],
                    Subtitle = StageSubtitles[i],
                    State = StatePending,
                    Summary = "等待上游完成"
                };
            }
            return result;
        }

        private static ProjectTelemetry GetOrLoad(string root)
        {
            string key = NormalizeRoot(root);
            ProjectTelemetry bucket;
            if (Projects.TryGetValue(key, out bucket)) return bucket;
            bucket = new ProjectTelemetry
            {
                Root = key,
                OverallState = "准备就绪",
                CurrentHeadline = "等待启动一键成片",
                ActiveStageIndex = -1,
                Stages = FreshStages()
            };
            var persisted = ReadPersisted(key);
            if (persisted != null)
            {
                bucket.RunId = persisted.RunId;
                bucket.OverallState = persisted.OverallState ?? bucket.OverallState;
                bucket.CurrentHeadline = persisted.CurrentHeadline ?? bucket.CurrentHeadline;
                bucket.CurrentDetail = persisted.CurrentDetail;
                bucket.ActiveStageIndex = persisted.ActiveStageIndex;
                bucket.AccumulatedRunMs = Math.Max(0L, persisted.TotalElapsedMs);
                if (persisted.Stages != null)
                {
                    foreach (var s in persisted.Stages)
                    {
                        if (s == null || s.Index < 0 || s.Index >= bucket.Stages.Length) continue;
                        var dst = bucket.Stages[s.Index];
                        dst.State = String.IsNullOrWhiteSpace(s.State) ? StatePending : s.State;
                        dst.Summary = s.Summary ?? dst.Summary;
                        dst.AccumulatedMs = Math.Max(0L, s.ElapsedMs);
                        dst.Done = s.Done;
                        dst.Total = s.Total;
                        dst.RepairedCount = s.RepairedCount;
                        dst.StartedUtc = s.StartedUtc;
                        dst.FinishedUtc = s.FinishedUtc;
                    }
                }
            }
            var ledger = FlowIncidentStore.Load(key);
            bucket.PreAssetIncidentCount = FlowIncidentStore.Filter(ledger, FlowHalf.PreAssets, false).Count;
            bucket.PostAssetIncidentCount = FlowIncidentStore.Filter(ledger, FlowHalf.PostAssets, false).Count;
            bucket.IncidentRevision = ledger.IncidentRevision;
            Projects[key] = bucket;
            return bucket;
        }

        internal static FlowRunTelemetryRecord ReadPersisted(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return null;
            try { return AgentData.Read<FlowRunTelemetryRecord>(AgentData.Inside(root, TelemetryFile)); }
            catch (Exception) { return null; }
        }

        private static void PersistLocked(ProjectTelemetry p)
        {
            if (String.IsNullOrWhiteSpace(p.Root) || !Directory.Exists(p.Root)) return;
            try
            {
                var now = DateTime.UtcNow;
                var record = new FlowRunTelemetryRecord
                {
                    RunId = p.RunId,
                    OverallState = p.OverallState,
                    CurrentHeadline = p.CurrentHeadline,
                    CurrentDetail = p.CurrentDetail,
                    ActiveStageIndex = p.ActiveStageIndex,
                    TotalElapsedMs = p.LiveTotalMs(now),
                    UpdatedUtc = AnalysisData.Now(),
                    Stages = p.Stages.Select(s => new FlowStageRecord
                    {
                        Index = s.Index,
                        Key = s.Key,
                        Title = s.Title,
                        Subtitle = s.Subtitle,
                        State = s.State,
                        Summary = s.Summary,
                        ElapsedMs = s.LiveMs(now),
                        Done = s.Done,
                        Total = s.Total,
                        RepairedCount = s.RepairedCount,
                        StartedUtc = s.StartedUtc,
                        FinishedUtc = s.FinishedUtc
                    }).ToList()
                };
                AgentData.Save(AgentData.Inside(p.Root, TelemetryFile), record);
            }
            catch (Exception) { }
        }

        private static void CloseActiveStageClockLocked(ProjectTelemetry p, DateTime now)
        {
            foreach (var s in p.Stages)
            {
                if (s.SegmentStartedUtc.HasValue)
                {
                    s.AccumulatedMs += Math.Max(0L, (long)(now - s.SegmentStartedUtc.Value).TotalMilliseconds);
                    s.SegmentStartedUtc = null;
                }
            }
        }

        private static void AppendTimelineLocked(ProjectTelemetry p, int stageIndex, string level, string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return;
            string safe = CodexAuthMode.RedactDiagnostic(message).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safe.Length == 0) return;
            p.Timeline.Add(new FlowTimelineEntry
            {
                Seq = ++p.TimelineSeq,
                Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                StageIndex = stageIndex,
                Level = String.IsNullOrWhiteSpace(level) ? "信息" : level,
                Message = AgentData.Short(safe, 260)
            });
            if (p.Timeline.Count > 80) p.Timeline.RemoveRange(0, p.Timeline.Count - 80);
            p.Revision++;
        }

        internal static void StartRun(string root, string runId, bool manualAssetReview)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                p.RunId = String.IsNullOrWhiteSpace(runId) ? AgentData.Id() : runId;
                p.IsRunning = true;
                p.OverallState = "制作中";
                p.CurrentHeadline = "正在启动自动流程";
                p.CurrentDetail = "校验剧本、制作方案与已保存断点…";
                if (!p.RunSegmentStartedUtc.HasValue) p.RunSegmentStartedUtc = now;
                p.WorkItems.Clear();
                if (!manualAssetReview && p.Stages[StageAssetReview].State == StatePending)
                {
                    p.Stages[StageAssetReview].Summary = "已配置自动交接，生成完成后直接进入分镜";
                }
                AppendTimelineLocked(p, StageAnalysis, "信息", "已启动一键成片自动化流程");
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void EnterStage(string root, int stageIndex, string headline, string detail = null)
        {
            if (String.IsNullOrWhiteSpace(root) || stageIndex < 0 || stageIndex >= StageKeys.Length) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                CloseActiveStageClockLocked(p, now);
                for (int i = 0; i < stageIndex; i++)
                {
                    if (p.Stages[i].State == StateRunning)
                    {
                        p.Stages[i].State = StateCompleted;
                        p.Stages[i].FinishedUtc = AnalysisData.Now();
                    }
                }
                var stage = p.Stages[stageIndex];
                stage.State = StateRunning;
                if (String.IsNullOrEmpty(stage.StartedUtc)) stage.StartedUtc = AnalysisData.Now();
                stage.SegmentStartedUtc = now;
                if (!String.IsNullOrWhiteSpace(detail)) stage.Summary = AgentData.Short(CodexAuthMode.RedactDiagnostic(detail), 180);
                else if (String.IsNullOrWhiteSpace(stage.Summary) || stage.Summary == "等待上游完成") stage.Summary = headline;
                p.ActiveStageIndex = stageIndex;
                p.IsRunning = true;
                if (!p.RunSegmentStartedUtc.HasValue) p.RunSegmentStartedUtc = now;
                p.OverallState = "制作中";
                p.CurrentHeadline = String.IsNullOrWhiteSpace(headline) ? stage.Title : headline;
                if (detail != null) p.CurrentDetail = AgentData.Short(CodexAuthMode.RedactDiagnostic(detail), 260);
                AppendTimelineLocked(p, stageIndex, "信息", "进入阶段 · " + stage.Title + (String.IsNullOrWhiteSpace(detail) ? "" : " · " + detail));
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void UpdateStage(string root, int stageIndex, string summary, int done = -1, int total = -1)
        {
            if (String.IsNullOrWhiteSpace(root) || stageIndex < 0 || stageIndex >= StageKeys.Length) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var stage = p.Stages[stageIndex];
                if (!String.IsNullOrWhiteSpace(summary))
                {
                    string safe = AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 200);
                    stage.Summary = safe;
                    if (p.ActiveStageIndex == stageIndex) p.CurrentDetail = safe;
                }
                if (done >= 0) stage.Done = done;
                if (total >= 0) stage.Total = total;
                p.Revision++;
            }
        }

        internal static void CompleteStage(string root, int stageIndex, string summary = null)
        {
            if (String.IsNullOrWhiteSpace(root) || stageIndex < 0 || stageIndex >= StageKeys.Length) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                var stage = p.Stages[stageIndex];
                if (stage.SegmentStartedUtc.HasValue)
                {
                    stage.AccumulatedMs += Math.Max(0L, (long)(now - stage.SegmentStartedUtc.Value).TotalMilliseconds);
                    stage.SegmentStartedUtc = null;
                }
                stage.State = StateCompleted;
                stage.FinishedUtc = AnalysisData.Now();
                if (!String.IsNullOrWhiteSpace(summary)) stage.Summary = AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 200);
                if (stage.Total > 0) stage.Done = stage.Total;
                AppendTimelineLocked(p, stageIndex, "完成", stage.Title + "已完成" + (String.IsNullOrWhiteSpace(summary) ? "" : " · " + summary));
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void SkipStage(string root, int stageIndex, string summary = null)
        {
            if (String.IsNullOrWhiteSpace(root) || stageIndex < 0 || stageIndex >= StageKeys.Length) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var stage = p.Stages[stageIndex];
                stage.SegmentStartedUtc = null;
                stage.State = StateSkipped;
                stage.Summary = String.IsNullOrWhiteSpace(summary) ? "按配置自动跳过" : AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 200);
                stage.FinishedUtc = AnalysisData.Now();
                AppendTimelineLocked(p, stageIndex, "信息", stage.Title + " · " + stage.Summary);
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void WaitReviewStage(string root, int stageIndex, string summary = null)
        {
            if (String.IsNullOrWhiteSpace(root) || stageIndex < 0 || stageIndex >= StageKeys.Length) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                CloseActiveStageClockLocked(p, now);
                if (p.RunSegmentStartedUtc.HasValue)
                {
                    p.AccumulatedRunMs += Math.Max(0L, (long)(now - p.RunSegmentStartedUtc.Value).TotalMilliseconds);
                    p.RunSegmentStartedUtc = null;
                }
                var stage = p.Stages[stageIndex];
                stage.State = StateWaiting;
                if (!String.IsNullOrWhiteSpace(summary)) stage.Summary = AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 220);
                p.ActiveStageIndex = stageIndex;
                p.IsRunning = false;
                p.OverallState = "待人工审核";
                p.CurrentHeadline = stage.Title + " · 等待你确认";
                p.CurrentDetail = stage.Summary;
                AppendTimelineLocked(p, stageIndex, "完成", stage.Title + "就绪，等待人工确认");
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void FailStage(string root, int stageIndex, string summary = null)
        {
            StopStage(root, stageIndex, summary, false);
        }

        internal static void PauseStage(string root, int stageIndex, string summary)
        {
            StopStage(root, stageIndex, summary, true);
        }

        private static void StopStage(string root, int stageIndex, string summary, bool paused)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                CloseActiveStageClockLocked(p, now);
                if (p.RunSegmentStartedUtc.HasValue)
                {
                    p.AccumulatedRunMs += Math.Max(0L, (long)(now - p.RunSegmentStartedUtc.Value).TotalMilliseconds);
                    p.RunSegmentStartedUtc = null;
                }
                int idx = stageIndex >= 0 && stageIndex < StageKeys.Length ? stageIndex : Math.Max(0, p.ActiveStageIndex);
                if (idx >= 0 && idx < StageKeys.Length)
                {
                    p.Stages[idx].State = paused ? "已暂停" : StateFailed;
                    if (!String.IsNullOrWhiteSpace(summary))
                        p.Stages[idx].Summary = AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 220);
                }
                p.IsRunning = false;
                p.OverallState = paused ? "已暂停" : "待续做";
                p.CurrentDetail = String.IsNullOrWhiteSpace(summary) ? p.CurrentDetail : AgentData.Short(CodexAuthMode.RedactDiagnostic(summary), 300);
                AppendTimelineLocked(p, idx, paused ? "信息" : "错误", "流程暂停 · " + (summary ?? "等待检查"));
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static void UpsertWorkItem(string root, string key, int stageIndex, string title, string detail,
            string state = "运行中", int done = 0, int total = 0)
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(key)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                FlowWorkItem item;
                if (!p.WorkItems.TryGetValue(key, out item))
                {
                    item = new FlowWorkItem { Key = key, StartedUtc = now };
                    p.WorkItems[key] = item;
                }
                item.StageIndex = stageIndex;
                item.StageTitle = stageIndex >= 0 && stageIndex < StageTitles.Length ? StageTitles[stageIndex] : "自动化流程";
                item.Title = AgentData.Short(CodexAuthMode.RedactDiagnostic(title ?? key), 120);
                item.Detail = AgentData.Short(CodexAuthMode.RedactDiagnostic(detail ?? ""), 220);
                item.State = String.IsNullOrWhiteSpace(state) ? "运行中" : state;
                item.Done = done;
                item.Total = total;
                item.UpdatedUtc = now;
                if (item.State == "已完成" || item.State == "已跳过" || item.State == "失败")
                    item.FinalElapsedMs = Math.Max(0L, (long)(now - item.StartedUtc).TotalMilliseconds);
                else
                    item.FinalElapsedMs = 0;

                if (p.WorkItems.Count > 40)
                {
                    var stale = p.WorkItems.Values
                        .Where(x => x.State == "已完成" || x.State == "已跳过")
                        .OrderBy(x => x.UpdatedUtc)
                        .Take(p.WorkItems.Count - 30)
                        .Select(x => x.Key)
                        .ToList();
                    foreach (var oldKey in stale) p.WorkItems.Remove(oldKey);
                }
                p.Revision++;
            }
        }

        internal static void FinishWorkItem(string root, string key, string detail = null, string state = "已完成")
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(key)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                FlowWorkItem item;
                if (!p.WorkItems.TryGetValue(key, out item)) return;
                var now = DateTime.UtcNow;
                item.State = String.IsNullOrWhiteSpace(state) ? "已完成" : state;
                if (detail != null) item.Detail = AgentData.Short(CodexAuthMode.RedactDiagnostic(detail), 220);
                item.UpdatedUtc = now;
                item.FinalElapsedMs = Math.Max(0L, (long)(now - item.StartedUtc).TotalMilliseconds);
                if (item.Total > 0 && item.State == "已完成") item.Done = item.Total;
                p.Revision++;
            }
        }

        internal static void Note(string root, int stageIndex, string level, string message)
        {
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(message)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                AppendTimelineLocked(p, stageIndex, level, message);
            }
        }

        internal static void RecordRepair(string root, int stageIndex, string half, string message)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                if (stageIndex >= 0 && stageIndex < p.Stages.Length) p.Stages[stageIndex].RepairedCount++;
                if (!String.IsNullOrWhiteSpace(message))
                    AppendTimelineLocked(p, stageIndex, "修复", message);
                p.Revision++;
            }
        }

        internal static void SyncIncidentCounts(string root, int preCount, int postCount, long incidentRevision)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                p.IncidentRevision = incidentRevision;
                p.PreAssetIncidentCount = preCount;
                p.PostAssetIncidentCount = postCount;
                p.Revision++;
            }
        }

        internal static bool IsActiveWork(string state)
        {
            return state == "运行中" || state == "进行中" || state == "核对中" || state == "重试中" || state == "自动修复中";
        }

        internal static void EndRun(string root, string finalState, string headline, string detail = null)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                CloseActiveStageClockLocked(p, now);
                if (p.RunSegmentStartedUtc.HasValue)
                {
                    p.AccumulatedRunMs += Math.Max(0L, (long)(now - p.RunSegmentStartedUtc.Value).TotalMilliseconds);
                    p.RunSegmentStartedUtc = null;
                }
                p.IsRunning = false;
                if (!String.IsNullOrWhiteSpace(finalState)) p.OverallState = finalState;
                if (!String.IsNullOrWhiteSpace(headline)) p.CurrentHeadline = headline;
                if (detail != null) p.CurrentDetail = AgentData.Short(CodexAuthMode.RedactDiagnostic(detail), 300);
                foreach (var w in p.WorkItems.Values.Where(x => IsActiveWork(x.State)))
                {
                    w.State = finalState == "已暂停" ? "已暂停" : finalState == "已完成" ? "已完成" : "已结束";
                    w.FinalElapsedMs = Math.Max(0L, (long)(now - w.StartedUtc).TotalMilliseconds);
                }
                AppendTimelineLocked(p, Math.Max(0, p.ActiveStageIndex), finalState == "已完成" ? "完成" : "信息",
                    (headline ?? finalState ?? "流程结束"));
                p.Revision++;
                PersistLocked(p);
            }
        }

        internal static FlowLiveSnapshot Snapshot(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return null;
            lock (Gate)
            {
                var p = GetOrLoad(root);
                var now = DateTime.UtcNow;
                var stages = new FlowStageSnapshot[p.Stages.Length];
                double weighted = 0.0;
                for (int i = 0; i < p.Stages.Length; i++)
                {
                    var s = p.Stages[i];
                    stages[i] = new FlowStageSnapshot
                    {
                        Index = s.Index,
                        Key = s.Key,
                        Title = s.Title,
                        Subtitle = s.Subtitle,
                        State = s.State,
                        Summary = s.Summary,
                        ElapsedMs = s.LiveMs(now),
                        Done = s.Done,
                        Total = s.Total,
                        RepairedCount = s.RepairedCount,
                        StartedUtc = s.StartedUtc,
                        FinishedUtc = s.FinishedUtc
                    };
                    if (s.State == StateCompleted || s.State == StateSkipped) weighted += 1.0;
                    else if (s.State == StateWaiting) weighted += 0.85;
                    else if (s.State == StateRunning)
                    {
                        double sub = s.Total > 0 ? Math.Min(0.95, Math.Max(0.12, (double)s.Done / s.Total)) : 0.35;
                        weighted += sub;
                    }
                }
                double pct = Math.Min(100.0, Math.Max(0.0, weighted * 100.0 / p.Stages.Length));
                if (p.OverallState == "已完成") pct = 100.0;
                long activeStageMs = p.ActiveStageIndex >= 0 && p.ActiveStageIndex < stages.Length
                    ? stages[p.ActiveStageIndex].ElapsedMs : 0L;
                var items = p.WorkItems.Values
                    .OrderByDescending(x => x.State == "自动修复中" || x.State == "核对中" || x.State == "重试中" ? 4 : IsActiveWork(x.State) ? 3 : x.State == "失败" ? 2 : 1)
                    .ThenByDescending(x => x.UpdatedUtc)
                    .Take(16)
                    .Select(x => x.Clone(now))
                    .ToArray();
                return new FlowLiveSnapshot
                {
                    ProjectRoot = p.Root,
                    RunId = p.RunId,
                    OverallState = p.OverallState,
                    CurrentHeadline = p.CurrentHeadline,
                    CurrentDetail = p.CurrentDetail,
                    ActiveStageIndex = p.ActiveStageIndex,
                    OverallPercent = pct,
                    TotalElapsedMs = p.LiveTotalMs(now),
                    ActiveStageElapsedMs = activeStageMs,
                    IsRunning = p.IsRunning,
                    Stages = stages,
                    ActiveItems = items,
                    Timeline = p.Timeline.AsEnumerable().Reverse().Take(40).ToArray(),
                    PreAssetIncidentCount = p.PreAssetIncidentCount,
                    PostAssetIncidentCount = p.PostAssetIncidentCount,
                    IncidentRevision = p.IncidentRevision,
                    Revision = p.Revision
                };
            }
        }

        internal static string FormatDuration(long ms)
        {
            if (ms < 0) ms = 0;
            int totalSeconds = (int)(ms / 1000L);
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;
            if (hours > 0)
                return String.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", hours, minutes, seconds);
            return String.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", minutes, seconds);
        }

        internal static string FormatDurationChinese(long ms)
        {
            if (ms < 1000) return "0 秒";
            int totalSeconds = (int)(ms / 1000L);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            if (minutes <= 0) return seconds + " 秒";
            return minutes + " 分 " + seconds + " 秒";
        }
    }
}
