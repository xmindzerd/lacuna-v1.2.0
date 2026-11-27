using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneClickFilm
{
    internal static class FlowHalf
    {
        internal const string PreAssets = "pre_assets";
        internal const string PostAssets = "post_assets";

        internal static string Label(string half, bool combined = false)
        {
            if (combined) return "全流程（资产前至最终提示词）";
            return half == PreAssets ? "前半段（剧本分析至资产出图）" : "后半段（分镜分段至最终提示词）";
        }
    }

    internal static class FlowIncidentCategory
    {
        internal const string ScriptAnalysisValidation = "剧本分析与视觉设定校验";
        internal const string AssetPreparationAndSchema = "视觉设定映射与基准态";
        internal const string AssetImageGeneration = "资产出图与回图";
        internal const string StoryboardValidation = "竖屏分镜与必需镜头校验";
        internal const string SegmentationConstraint = "分段时长与参考图上限";
        internal const string FinalPromptValidation = "最终提示词与参考绑定校验";
        internal const string TransportAndTransient = "网络通道与临时抖动";
        internal const string UnexpectedRuntimeError = "运行异常";
    }

    public sealed class FlowIncident
    {
        public string Id { get; set; }
        public string RunId { get; set; }
        public string OccurredUtc { get; set; }
        public string Half { get; set; }
        public int StageIndex { get; set; }
        public string StageKey { get; set; }
        public string StageTitle { get; set; }
        public string SubStep { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorType { get; set; }
        public string Summary { get; set; }
        public string RawErrorMessage { get; set; }
        public string ContextSnapshot { get; set; }
        public string SourceHint { get; set; }
        public string AutoRepairStrategy { get; set; }
        public string CodexDiagnosis { get; set; }
        public string CodexFixAction { get; set; }
        public string PermanentFixSuggestion { get; set; }
        public bool Resolved { get; set; }
        public long ResolutionMs { get; set; }
        public int AttemptsUsed { get; set; }
        public string Channel { get; set; }
        public string Model { get; set; }
    }

    public sealed class FlowReportRecord
    {
        public string Half { get; set; }
        public string RunId { get; set; }
        public string GeneratedUtc { get; set; }
        public string MarkdownRelativePath { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public int IncidentCount { get; set; }
        public int ResolvedCount { get; set; }
        public int UnresolvedCount { get; set; }
        public bool CombinedAllStages { get; set; }
        public string IncidentDigest { get; set; }
        public bool SynthesizedByCodex { get; set; }
    }

    public sealed class FlowIncidentLedger
    {
        public string CurrentRunId { get; set; }
        public long IncidentRevision { get; set; }
        public string LastUpdatedUtc { get; set; }
        public string PreAssetsRunId { get; set; }
        public string PostAssetsRunId { get; set; }
        public List<FlowIncident> Incidents { get; set; }
        public FlowReportRecord PreAssetsReport { get; set; }
        public FlowReportRecord PostAssetsReport { get; set; }
    }

    internal static class FlowIncidentStore
    {
        internal const string RelativeFolder = @"一键成片\问题报告";
        internal const string LedgerFileName = "incidents.json";

        private static string MutexName(string root)
        {
            string normalized = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant();
            return "Lacuna.FlowIncidents." + AgentData.Hash(normalized);
        }

        internal static string Folder(string root)
        {
            return AgentData.Inside(root, RelativeFolder);
        }

        internal static string LedgerPath(string root)
        {
            return AgentData.Inside(root, Path.Combine(RelativeFolder, LedgerFileName));
        }

        internal static FlowIncidentLedger Load(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return EmptyLedger();
            try
            {
                var ledger = AgentData.Read<FlowIncidentLedger>(LedgerPath(root));
                if (ledger == null) return EmptyLedger();
                if (ledger.Incidents == null) ledger.Incidents = new List<FlowIncident>();
                return ledger;
            }
            catch (Exception) { return EmptyLedger(); }
        }

        private static FlowIncidentLedger EmptyLedger()
        {
            return new FlowIncidentLedger { Incidents = new List<FlowIncident>() };
        }

        private static void Edit(string root, Action<FlowIncidentLedger> mutate)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            using (var gate = new Mutex(false, MutexName(root)))
            {
                try { gate.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    var ledger = Load(root);
                    mutate(ledger);
                    ledger.IncidentRevision++;
                    ledger.LastUpdatedUtc = AnalysisData.Now();
                    Directory.CreateDirectory(Folder(root));
                    AgentData.Save(LedgerPath(root), ledger);
                    SyncCounts(root, ledger);
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static void SyncCounts(string root, FlowIncidentLedger ledger)
        {
            int pre = Filter(ledger, FlowHalf.PreAssets, false).Count;
            int post = Filter(ledger, FlowHalf.PostAssets, false).Count;
            FlowTelemetry.SyncIncidentCounts(root, pre, post, ledger.IncidentRevision);
        }

        internal static void BeginHalf(string root, string runId, string half, bool resetPreviousForHalf = false)
        {
            if (String.IsNullOrWhiteSpace(root)) return;
            string effectiveRun = String.IsNullOrWhiteSpace(runId) ? AgentData.Id() : runId;
            Edit(root, ledger =>
            {
                ledger.CurrentRunId = effectiveRun;
                if (half == FlowHalf.PreAssets)
                {
                    if (resetPreviousForHalf || String.IsNullOrWhiteSpace(ledger.PreAssetsRunId))
                        ledger.PreAssetsRunId = effectiveRun;
                    if (resetPreviousForHalf) ledger.PreAssetsReport = null;
                }
                else
                {
                    if (resetPreviousForHalf || String.IsNullOrWhiteSpace(ledger.PostAssetsRunId))
                        ledger.PostAssetsRunId = effectiveRun;
                    if (resetPreviousForHalf) ledger.PostAssetsReport = null;
                }
            });
        }

        internal static FlowIncident Record(string root, FlowIncident incident)
        {
            if (String.IsNullOrWhiteSpace(root) || incident == null) return incident;
            Sanitize(incident);
            Edit(root, ledger =>
            {
                if (String.IsNullOrWhiteSpace(incident.Id)) incident.Id = AgentData.Id();
                if (String.IsNullOrWhiteSpace(incident.OccurredUtc)) incident.OccurredUtc = AnalysisData.Now();
                if (String.IsNullOrWhiteSpace(incident.Half)) incident.Half = FlowHalf.PreAssets;
                if (String.IsNullOrWhiteSpace(incident.RunId))
                {
                    incident.RunId = incident.Half == FlowHalf.PreAssets
                        ? (ledger.PreAssetsRunId ?? ledger.CurrentRunId ?? AgentData.Id())
                        : (ledger.PostAssetsRunId ?? ledger.CurrentRunId ?? AgentData.Id());
                }
                if (incident.Half == FlowHalf.PreAssets && String.IsNullOrWhiteSpace(ledger.PreAssetsRunId))
                    ledger.PreAssetsRunId = incident.RunId;
                if (incident.Half == FlowHalf.PostAssets && String.IsNullOrWhiteSpace(ledger.PostAssetsRunId))
                    ledger.PostAssetsRunId = incident.RunId;
                if (String.IsNullOrWhiteSpace(incident.Channel))
                    incident.Channel = CodexAuthMode.IsApi ? "Responses API" : "Codex 账号";
                if (String.IsNullOrWhiteSpace(incident.Model))
                    incident.Model = CodexAuthMode.EffectiveModel(AgentData.Options().Model);
                if (String.IsNullOrWhiteSpace(incident.Severity))
                    incident.Severity = incident.Resolved ? "阻塞已解决" : "待人工介入";
                int previous = ledger.Incidents.FindIndex(i => i.Id == incident.Id && i.RunId == incident.RunId);
                if (previous >= 0) ledger.Incidents[previous] = incident;
                else ledger.Incidents.Add(incident);
                if (ledger.Incidents.Count > 200)
                    ledger.Incidents.RemoveRange(0, ledger.Incidents.Count - 200);
            });
            FlowTelemetry.RecordRepair(root, incident.StageIndex, incident.Half,
                (incident.Resolved ? "已自动解决 · " : "发现问题 · ") + incident.StageTitle + " · " + incident.Summary);
            if (FlowCreation.Development)
                DevMonitor.Log(root, "TASK", "[问题记录] " + incident.StageTitle + " · " + incident.Summary +
                    (incident.Resolved ? "（已自动解决）" : "（未解决）"));
            return incident;
        }

        internal static Exception MarkRecorded(Exception error, int stageIndex)
        {
            error.Data["flowRecorded"] = true;
            error.Data["flowStage"] = stageIndex;
            return error;
        }

        internal static bool IsRecorded(Exception error)
        {
            if (error == null) return false;
            if (Equals(error.Data["flowRecorded"], true)) return true;
            var aggregate = error as AggregateException;
            return aggregate == null ? IsRecorded(error.InnerException) : aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsRecorded);
        }

        internal static bool ResolveLatestUnresolved(string root, string half, int stageIndex, string subStep,
            string fixAction, long elapsedMs, int attempts)
        {
            return ResolveIncident(root, half, stageIndex, subStep, fixAction, elapsedMs, attempts, null);
        }

        internal static bool ResolveIncident(string root, string half, int stageIndex, string subStep,
            string fixAction, long elapsedMs, int attempts, string incidentId)
        {
            bool updated = false;
            Edit(root, ledger =>
            {
                var incident = Filter(ledger, half, false).LastOrDefault(i =>
                    !i.Resolved && i.StageIndex == stageIndex && (incidentId == null ? i.SubStep == Redact(subStep, 160) : i.Id == incidentId));
                if (incident == null) return;
                incident.Resolved = true;
                incident.Severity = "阻塞已解决";
                incident.CodexFixAction = Redact(fixAction, 1200);
                incident.ResolutionMs += Math.Max(0, elapsedMs);
                incident.AttemptsUsed = Math.Max(incident.AttemptsUsed, attempts);
                updated = true;
            });
            return updated;
        }

        internal static void SaveReportRecord(string root, FlowReportRecord record)
        {
            if (String.IsNullOrWhiteSpace(root) || record == null) return;
            Edit(root, ledger =>
            {
                if (record.Half == FlowHalf.PreAssets) ledger.PreAssetsReport = record;
                else ledger.PostAssetsReport = record;
            });
        }

        internal static List<FlowIncident> Query(string root, string half, bool includePreWhenCombined = false)
        {
            var ledger = Load(root);
            return Filter(ledger, half, includePreWhenCombined);
        }

        internal static List<FlowIncident> Filter(FlowIncidentLedger ledger, string half, bool includePreWhenCombined)
        {
            if (ledger == null || ledger.Incidents == null) return new List<FlowIncident>();
            if (half == FlowHalf.PreAssets)
            {
                string run = ledger.PreAssetsRunId;
                return ledger.Incidents.Where(i => i != null && i.Half == FlowHalf.PreAssets &&
                    (String.IsNullOrWhiteSpace(run) || String.IsNullOrWhiteSpace(i.RunId) || i.RunId == run)).ToList();
            }
            string postRun = ledger.PostAssetsRunId;
            string preRun = ledger.PreAssetsRunId;
            return ledger.Incidents.Where(i => i != null && (
                (i.Half == FlowHalf.PostAssets && (String.IsNullOrWhiteSpace(postRun) || String.IsNullOrWhiteSpace(i.RunId) || i.RunId == postRun)) ||
                (includePreWhenCombined && i.Half == FlowHalf.PreAssets && (String.IsNullOrWhiteSpace(preRun) || String.IsNullOrWhiteSpace(i.RunId) || i.RunId == preRun))
            )).ToList();
        }

        internal static FlowReportRecord GetReport(string root, string half)
        {
            var ledger = Load(root);
            return half == FlowHalf.PreAssets ? ledger.PreAssetsReport : ledger.PostAssetsReport;
        }

        internal static string ReportMarkdownPath(string root, string half)
        {
            var report = GetReport(root, half);
            if (report != null && !String.IsNullOrWhiteSpace(report.MarkdownRelativePath))
            {
                try
                {
                    string full = AgentData.Inside(root, report.MarkdownRelativePath);
                    if (File.Exists(full)) return full;
                }
                catch (Exception) { }
            }
            string defaultName = half == FlowHalf.PreAssets ? "前半段问题报告.md" : "后半段问题报告.md";
            return AgentData.Inside(root, Path.Combine(RelativeFolder, defaultName));
        }

        private static string Redact(string value, int maxLen)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            return AgentData.Short(CodexAuthMode.RedactDiagnostic(value), maxLen);
        }

        private static void Sanitize(FlowIncident incident)
        {
            incident.StageTitle = Redact(incident.StageTitle, 80);
            incident.SubStep = Redact(incident.SubStep, 160);
            incident.Category = Redact(incident.Category, 80);
            incident.Severity = Redact(incident.Severity, 40);
            incident.ErrorCode = Redact(incident.ErrorCode, 60);
            incident.ErrorType = Redact(incident.ErrorType, 80);
            incident.Summary = Redact(incident.Summary, 280);
            incident.RawErrorMessage = Redact(incident.RawErrorMessage, 1600);
            incident.ContextSnapshot = Redact(incident.ContextSnapshot, 2400);
            incident.SourceHint = Redact(incident.SourceHint, 180);
            incident.AutoRepairStrategy = Redact(incident.AutoRepairStrategy, 120);
            incident.CodexDiagnosis = Redact(incident.CodexDiagnosis, 1200);
            incident.CodexFixAction = Redact(incident.CodexFixAction, 1200);
            incident.PermanentFixSuggestion = Redact(incident.PermanentFixSuggestion, 1200);
            incident.Channel = Redact(incident.Channel, 80);
            incident.Model = Redact(incident.Model, 80);
        }
    }
}
