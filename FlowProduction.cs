using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneClickFilm
{
    internal delegate string FlowImages(AnalysisVersion version, AnalysisResult analysis, AssetWork work, string[] ids,
        AssetChannel channel, string mode, Action<string, int, int> progress, CancellationToken token);

    internal static class FlowProduction
    {
        private static SelectedSkill Skill(string root, string operation)
        {
            return LocalSkills.Resolve(root, LocalSkills.Choice(root, operation));
        }

        internal static void Run(string root, string exe, string model, string approval, FlowImages images,
            Action<string> progress, CancellationToken cancel, Func<bool> exiting, string retryAttempt = null, Action<int,int> analysisCount = null)
        {
            using (FlowCreation.Lock(root))
            {
                var config = FlowCreation.Config(root);
                var state = FlowCreation.State(root);
                Action checkpoint = () => { cancel.ThrowIfCancellationRequested(); if (exiting()) throw new OperationCanceledException(); };
                Action<string> phase = title =>
                {
                    checkpoint();
                    DevMonitor.Progress(root,title);
                    state.Phase = title; state.Status = "制作中"; state.Detail = null;
                    FlowCreation.Save(root, state); progress(title);
                };
                var ledger = FlowIncidentStore.Load(root);
                string runId = ledger.PreAssetsRunId ?? AgentData.Id();
                bool manual = config != null && config.Manual;
                string half = FlowHalf.PreAssets;
                FlowTelemetry.StartRun(root,runId,manual);
                try
                {
                    if(FlowCreation.IsRegularOutput(root)){
                        half=FlowHalf.PostAssets;
                        FlowIncidentStore.BeginHalf(root,runId,half);
                        FlowTelemetry.EnterStage(root,FlowTelemetry.StagePrompts,"正在导出提示词");
                        if (config == null || config.Video == null)
                            throw new InvalidDataException("项目视频配置不完整，请在视频设置中重新保存。");
                        using(new ProjectTaskLease(root,"影片制作")){
                            var manualBook=Film.Open(root);
                            var target=VideoPlatform.For(config.Video,manualBook.Brief.Ratio,cancel);
                            FinishOutput(root,manualBook,state,config.Video,target,approval,progress,cancel,checkpoint,phase);
                        }
                        return;
                    }
                    FlowIncidentStore.BeginHalf(root,runId,half,ledger.PreAssetsRunId == null);
                    FlowTelemetry.EnterStage(root,FlowTelemetry.StageAnalysis,"剧本分析");
                    var source = AnalysisData.CurrentScript(root);
                    if (config == null || source == null || source.Hash != config.ScriptHash)
                        throw new InvalidDataException("创建项目时绑定的剧本已变化。请用原稿续做，或用新剧本创建项目。");
                    bool v2 = config.IsV2Builtin;
                    phase(FlowSkills.Titles[0]);
                    var version = Analyze(root, exe, model, source, cancel, progress, analysisCount, v2);
                    FlowTelemetry.CompleteStage(root,FlowTelemetry.StageAnalysis,"剧本分析完成");
                    var analysis = AnalysisData.ReadResult(root, version);
                    if (analysisCount != null) analysisCount(0,0);
                    FlowTelemetry.EnterStage(root,FlowTelemetry.StageAssets,"视觉设定整理及资产出图");
                    phase(v2 ? "视觉设定整理" : FlowSkills.Titles[1]);
                    using (new ProjectTaskLease(root, "准备提示词"))
                    {
                        if (v2)
                        {
                            var work = AssetProduction.Load(root, version, analysis);
                            AssetProduction.PopulateDirectImageSpecs(root, work, analysis);
                            AssetProduction.Save(root, work);
                        }
                        else Prepare(root, exe, model, source, version, analysis, cancel, progress);
                    }
                    phase(FlowSkills.Titles[2]);
                    using (new ProjectTaskLease(root, "出图"))
                        MakeImages(root, config, version, analysis, images, progress, cancel, checkpoint, retryAttempt);
                    checkpoint();
                    FlowTelemetry.CompleteStage(root,FlowTelemetry.StageAssets,"资产已就绪");
                    string assetKey;
                    using (AnalysisData.Lock(root))
                    {
                        var work = AssetProduction.Load(root, version, analysis);
                        string key = ReviewKey(root, work);
                        assetKey=key;
                        if (config.Manual && state.ReviewKey != key && approval != key)
                        {
                            state.Phase = "资产审核"; state.Status = "待人工审核";
                            state.Detail = "资产图已全部生成（共 " + work.Items.Count + " 项）。请人工查看资产图，确认后点击「一键确认全部资产并继续」自动完成分镜、分段与最终提示词。";
                            FlowTelemetry.WaitReviewStage(root,FlowTelemetry.StageAssetReview,state.Detail);
                            FlowCreation.Save(root, state); progress(state.Detail);
                            return;
                        }
                        if (config.Manual)
                        {
                            state.ReviewKey = key;
                            FlowTelemetry.CompleteStage(root,FlowTelemetry.StageAssetReview,"资产已人工确认交接");
                        }
                        else FlowTelemetry.SkipStage(root,FlowTelemetry.StageAssetReview,"已配置自动交接，跳过资产确认");
                        Deliver(root, work, config.Manual);
                        FlowCreation.Save(root, state);
                    }
                    half=FlowHalf.PostAssets;
                    string postId=AgentData.Hash(runId + "/" + assetKey);
                    FlowIncidentStore.BeginHalf(root,postId,half,ledger.PostAssetsRunId != postId);
                    FlowTelemetry.EnterStage(root,FlowTelemetry.StageStoryboard,"分镜与分段");
                    using (new ProjectTaskLease(root, "影片制作"))
                    {
                        var book = Film.Open(root);
                        VideoDelivery.RequireAlignedRatio(root,book.Brief.Ratio);
                        if (book.SyncRequired) Film.Save(root, book);
                        var scenes = book.Scenes.Where(s => s.Included).ToList();
                        if (scenes.Count == 0) throw new InvalidDataException("没有可拍摄的场次，请核对剧本分析。");
                        phase("分镜、分段与片段提示词");
                        var video=config.Video ?? new VideoOptions();
                        var target=VideoPlatform.For(video,config.Ratio,cancel);
                        FinishScenes(root, exe, model, book, scenes, cancel, progress,target);
                        FinishOutput(root,book,state,video,target,approval,progress,cancel,checkpoint,phase);
                    }
                }
                catch (Exception e)
                {
                    var agg = e as AggregateException;
                    var cause = agg != null ? agg.Flatten().InnerExceptions.FirstOrDefault() ?? e : e.GetBaseException();
                    string detailMsg = agg != null && agg.Flatten().InnerExceptions.Count > 1
                        ? String.Join("；", agg.Flatten().InnerExceptions.Select(x => x.Message).Distinct())
                        : e.InnerException is AggregateException ? e.Message : cause.Message;
                    state.Status = (cancel.IsCancellationRequested || cause is OperationCanceledException) ? "已暂停" : "待续做";
                    state.Detail = state.Status == "已暂停" ? "进度已保存，继续时从已完成的成果接续。" : AgentData.Short(FlowAutoRepair.Safe(detailMsg), 1200);
                    FlowCreation.Save(root, state);
                    var snapshot=FlowTelemetry.Snapshot(root);
                    int stage=cause.Data["flowStage"] is int ? (int)cause.Data["flowStage"] : Math.Max(0,snapshot.ActiveStageIndex);
                    if (state.Status == "已暂停") FlowTelemetry.PauseStage(root,stage,state.Detail);
                    else FlowTelemetry.FailStage(root,stage,state.Detail);
                    FlowTelemetry.EndRun(root,state.Status,state.Phase,state.Detail);
                    if (state.Status != "已暂停")
                    {
                        if (!FlowIncidentStore.IsRecorded(e))
                            FlowIncidentStore.Record(root,new FlowIncident { Half=half,StageIndex=stage,StageTitle=state.Phase,
                                SubStep=state.Phase, Category=FlowAutoRepair.IsTransient(e) ? FlowIncidentCategory.TransportAndTransient : FlowIncidentCategory.UnexpectedRuntimeError,
                                Summary=state.Detail,RawErrorMessage=FlowAutoRepair.Safe(detailMsg),ContextSnapshot=state.Phase,
                                SourceHint="FlowProduction.Run", ErrorType=cause.GetType().Name, Model=model,AutoRepairStrategy="保留断点",
                                PermanentFixSuggestion="复核该步骤的失败条件与恢复路径，加入断点续做用例。" });
                    }
                    throw;
                }
            }
        }

        private static void FinishOutput(string root,FilmBook book,FlowState state,VideoOptions video,VideoModel target,
            string approval,Action<string> progress,CancellationToken cancel,Action checkpoint,Action<string> phase)
        {
            var scenes=book.Scenes.Where(s=>s.Included).ToList();
            phase(FlowSkills.Titles[5]);
            checkpoint();
            if (state.FilmRevision != book.Revision || String.IsNullOrWhiteSpace(state.Export) ||
                !File.Exists(AgentData.Inside(root, Path.Combine(state.Export, "Seedance 2.5 交接清单.json"))))
            {
                string dir = FilmWriter.Export(root, book, true);
                state.Export = dir.Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length + 1);
                state.FilmRevision = book.Revision;
            }
            state.Detail = scenes.Count + " 场 · " + scenes.Sum(s => s.Shots.Count) + " 镜 · " +
                scenes.Sum(s => s.Segments.Count) + " 段，最终提示词已保存。";
            FlowTelemetry.CompleteStage(root,FlowTelemetry.StagePrompts,"最终提示词已导出");
            checkpoint();
            if (video.Review || video.Enabled)
            {
                var clips=VideoDelivery.Plan(root,book,video,target);
                string key=AgentData.Hash(String.Join("|",clips.Select(c=>c.Key)));
                // Equivalent platform bindings can still originate from different
                // approved manuscripts or skills. Review exactly the displayed text.
                var faithful = scenes.SelectMany(s => s.Segments).Select(g => g.History[g.Accepted])
                    .Where(p => p.SkillFaithful).Select(p => new { p.Text, p.SkillId, p.SkillDigest }).ToList();
                if (faithful.Count > 0) key=AgentData.Hash(key + AssetProduction.StableJson(faithful));
                state.PendingKey=key;
                if (video.Review && state.PromptKey!=key && approval!="review/"+key)
                {
                    state.Phase="提示词审核"; state.Status="待人工审核";
                    state.Detail+=" 请在提示词监看台逐段查阅，点击「通过全部提示词审核」继续。";
                    FlowTelemetry.WaitReviewStage(root,FlowTelemetry.StagePromptReview,state.Detail);
                    FlowCreation.Save(root,state); progress(state.Detail); return;
                }
                if (video.Review)
                {
                    state.PromptKey=key;
                    FlowTelemetry.CompleteStage(root,FlowTelemetry.StagePromptReview,"已通过提示词确认");
                }
                else FlowTelemetry.SkipStage(root,FlowTelemetry.StagePromptReview,"已配置跳过提示词审核");
                if (video.Enabled)
                {
                    int complete=clips.Count(c=>NovelVideo.Returned(root,NovelVideo.Read(root,c.Key)));
                    state.Status=complete==clips.Count?"已完成":"待确认费用";state.Phase="视频生成";
                    state.Detail="已取回 "+complete+" / "+clips.Count+" 段。打开视频生成页选择片段并确认平台报价。";
                    FlowTelemetry.EndRun(root,state.Status,state.Phase,state.Detail);
                    FlowCreation.Save(root,state);progress(state.Detail);return;
                }
            }
            if (!video.Review && !video.Enabled) FlowTelemetry.SkipStage(root,FlowTelemetry.StagePromptReview,"已配置跳过提示词审核");
            FlowTelemetry.EndRun(root,"已完成","最终提示词已交接",state.Detail);
            state.Status="已完成";FlowCreation.Save(root,state);progress(state.Detail);
        }

        private static AnalysisVersion Analyze(string root, string exe, string model, ScriptDocument script,
            CancellationToken token, Action<string> report, Action<int,int> count = null, bool v2 = false)
        {
            AnalysisVersion version;
            using (new ProjectTaskLease(root, "剧本分析"))
            {
                var ledger = AnalysisData.Load(root);
                version = ledger.Current(script);
                if (version == null)
                {
                    var skill = Skill(root, LocalSkills.StageKey(0));
                    ledger.LastRun = new AnalysisRun { State = "分析中", Source = script.Source, SourceHash = script.Hash,
                        Utc = AnalysisData.Now(), Skill = skill == null ? null : skill.Reference };
                    AnalysisData.RecordRun(root, ledger.LastRun);
                    try
                    {
                        Action<int,int> onPart = (n,total) => { if (count != null) count(n,total); else report("已分析剧本 " + n + " / " + total); };
                        var result = v2
                            ? AnalysisData.RunV2FlowPipeline(exe, root, script, model, onPart, token, resumeParts: true, detail: report)
                            : AnalysisData.Run(exe, root, script, model, onPart, token, skill, resumeParts: true, detail: report);
                        token.ThrowIfCancellationRequested();
                        AnalysisData.Publish(root, script, ledger, result);
                        AnalysisData.ClearCheckpoint(root);
                    }
                    catch (Exception error)
                    {
                        bool stopped = token.IsCancellationRequested || error is OperationCanceledException;
                        AnalysisData.RecordInterruption(root, script, skill, stopped ? "分析已停止" : "分析未完成",
                            stopped ? "已保存核对完成的分段，继续时从断点接续。" : AgentData.Short(error.Message, 700));
                        throw;
                    }
                    version = ledger.Current(script);
                }
                if (ledger.Confirmed(script) == version) return version;
            }
            token.ThrowIfCancellationRequested();
            AnalysisData.Confirm(root, script, version.Id);
            return version;
        }

        private static void Prepare(string root, string exe, string model, ScriptDocument source,
            AnalysisVersion version, AnalysisResult analysis, CancellationToken token, Action<string> report)
        {
            var work = AssetProduction.Load(root, version, analysis);
            string style = work.Style;
            var missing = work.Items.Where(i => String.IsNullOrWhiteSpace(i.Prompt) && String.IsNullOrWhiteSpace(i.Conflict) ||
                work.PendingPromptIds != null && work.PendingPromptIds.Contains(i.Id)).ToList();
            using (new PromptWriteLeases(root, work.Items.Where(i => !AssetProduction.PromptReady(i) || missing.Contains(i)).Select(i => i.Id)))
            {
                var skill = Skill(root, LocalSkills.StageKey(1));
                if (missing.Count > 0)
                    using (AssetUsageLedger.Begin(root, version.Id, AssetUsageLedger.Prompt))
                    AssetProduction.Prepare(exe, model, source, style, analysis, missing, skill, token,
                        n => report("已保存资产提示词 " + n + " / " + missing.Count), drafts =>
                        {
                            if (work.PendingPromptIds != null) { work.PendingPromptIds.RemoveAll(id=>drafts.Any(d=>d.id==id)); if(work.PendingPromptIds.Count==0) { work.PendingPromptIds=null;work.PromptRequestIds=null; } }
                            foreach (var d in drafts) AssetProduction.StoreDraft(missing.First(i => i.Id == d.id), d, skill, style);
                            AssetProduction.Save(root, work);
                        }, assetCanvas: ImageOutputSettings.DefaultAssetRatio);
            }
            if (work.Items.All(AssetProduction.PromptReady)) return;
            AssetProduction.AutoReviewBlocked(root, version, analysis, source, exe, model, null, token, report);
            var remaining = AssetProduction.Load(root, version, analysis).Items.Where(i => !AssetProduction.PromptReady(i)).ToList();
            if (remaining.Count > 0) throw new InvalidDataException(String.Join("；", remaining.Select(i =>
                i.Source.name + " · " + (i.PendingRepair == null ? i.Conflict : i.PendingRepair.changes))) + "。可在资产详情补充决定后继续。");
        }

        private static bool HasImage(AssetItem item)
        {
            return item.Active != null && item.Active.Path != null && (item.Active.State == "待审图" || item.Active.State == "已通过");
        }

        private static void MakeImages(string root, FlowConfig config, AnalysisVersion version, AnalysisResult analysis,
            FlowImages images, Action<string> report, CancellationToken token, Action checkpoint, string retryAttempt)
        {
            int recoveryRounds = 0;
            while (true)
            {
                checkpoint();
                var work = AssetProduction.Load(root, version, analysis);
                var missing = work.Items.Where(i => !HasImage(i)).Select(i => i.Id).ToArray();
                if (missing.Length == 0) return;
                var batch = work.ImageBatch;
                bool resume = batch != null && batch.NeedsAction;
                int completedBefore = batch == null ? 0 : batch.Jobs.Count(j => j.Done);
                if (resume)
                {
                    var waiting = batch.Jobs.Where(j => !j.Done && j.Attempt != null && j.ImageIncidentId == null).ToList();
                    AssetProduction.ReconcileBatch(root, work, batch, token);
                    foreach (var job in waiting.Where(j => j.Done))
                        FlowIncidentStore.ResolveLatestUnresolved(root, FlowHalf.PreAssets, FlowTelemetry.StageAssets, job.Name,
                            "核对原任务后收到回图。", 0, job.RetryCount);
                    foreach (var job in batch.Jobs.Where(j => j.IsBase && j.Done && j.Attempt.State == "待审图"))
                    {
                        AssetProduction.AcceptFlowBase(root, work, batch, job);
                    }
                    AssetProduction.Save(root, work);
                    if (!batch.NeedsAction) continue;
                }
                string mode = resume ? "恢复" : null;
                if (resume && retryAttempt != null)
                {
                    var target = AssetProduction.TimedOutJobs(root, batch).FirstOrDefault(j => j.Attempt.Id == retryAttempt);
                    if (target == null || target.Attempt.Id != retryAttempt)
                        throw new InvalidOperationException("出图任务已变化，请重新打开项目核对。");
                    mode = "单项重试/" + retryAttempt; recoveryRounds++; retryAttempt = null;
                }
                else if (resume && !AssetProduction.ReadyJobs(batch).Any())
                {
                    var failed = batch.Jobs.Where(j => !j.Done && j.Attempt != null).ToArray();
                    if (failed.Length == 0 || failed.Any(j => !SafeImageRetry(j)) || recoveryRounds >= 3)
                        throw ImageBatchFailure(root, batch, AssetProduction.PendingSummary(batch) + " 请在资产页核对原任务后继续。");
                    mode = "重新生成"; recoveryRounds++;
                }
                var retryErrors=mode == "重新生成" ? batch.Jobs.Where(j => !j.Done && j.Attempt != null)
                    .ToDictionary(j => j.IsBase ? "base/"+j.GroupId : "item/"+j.ItemId,j => FlowAutoRepair.Safe(j.Attempt.Detail)) : null;
                var retryClock=System.Diagnostics.Stopwatch.StartNew();
                string result = images(version, analysis, work, missing, FlowChannels.Channel(config.Channel), mode,
                    (msg,n,total) => report(total > 0 ? msg + " · " + n + "/" + total : msg), token);
                checkpoint();
                var saved = AssetProduction.Load(root, version, analysis);
                if (mode == "重新生成")
                {
                    foreach (string id in retryErrors.Keys)
                    {
                        var current=saved.ImageBatch == null ? null : saved.ImageBatch.Jobs.FirstOrDefault(j => (j.IsBase ? "base/"+j.GroupId : "item/"+j.ItemId) == id);
                        if (current == null || !current.Done) continue;
                        if (current.ImageIncidentId != null)
                        {
                            FlowIncidentStore.ResolveIncident(root, FlowHalf.PreAssets, FlowTelemetry.StageAssets, current.Name,
                                "重新生成并核对回图。", retryClock.ElapsedMilliseconds, current.RetryCount, current.ImageIncidentId);
                            continue;
                        }
                        if (FlowIncidentStore.ResolveLatestUnresolved(root, FlowHalf.PreAssets, FlowTelemetry.StageAssets,
                            current.Name, "重新生成并核对回图。", retryClock.ElapsedMilliseconds, current.RetryCount)) continue;
                        FlowIncidentStore.Record(root,new FlowIncident { Half=FlowHalf.PreAssets,StageIndex=1,StageTitle="资产出图",
                            Category=FlowIncidentCategory.AssetImageGeneration,SubStep=current.Name,SourceHint="FlowProduction.MakeImages",
                            Summary="已核对可重试的资产任务恢复成功",RawErrorMessage=retryErrors[id],ContextSnapshot=current.Name,
                            Resolved=true,AutoRepairStrategy="核对回执后恢复",CodexFixAction="重新生成并核对回图。",
                            PermanentFixSuggestion="覆盖已确认失败、回图恢复与外部提交不确定三种任务状态。",AttemptsUsed=recoveryRounds,
                            ResolutionMs=retryClock.ElapsedMilliseconds });
                    }
                }
                if (saved.Items.All(HasImage)) return;
                if (saved.ImageBatch == null || !saved.ImageBatch.NeedsAction)
                {
                    if (saved.Items.Count(i => !HasImage(i)) < missing.Length) continue;
                    throw new InvalidOperationException(result);
                }
                if (saved.ImageBatch.Jobs.Any(j => j.IsBase && j.Done && j.Attempt.State == "待审图") ||
                    AssetProduction.ReadyJobs(saved.ImageBatch).Any() && saved.ImageBatch.Jobs.Count(j => j.Done) > completedBefore) continue;
                var pending=saved.ImageBatch.Jobs.Where(j => !j.Done && j.Attempt != null).ToList();
                if (recoveryRounds < 3 && pending.Count > 0 && pending.All(SafeImageRetry) &&
                    !AssetProduction.ReadyJobs(saved.ImageBatch).Any()) continue;
                throw ImageBatchFailure(root, saved.ImageBatch, result + "。请核对后继续制作。");
            }
        }

        private static Exception ImageBatchFailure(string root, AssetImageBatch batch, string message)
        {
            var error = new InvalidOperationException(message);
            var recorded = FlowIncidentStore.Query(root, FlowHalf.PreAssets).Where(i =>
                !i.Resolved && i.Category == FlowIncidentCategory.AssetImageGeneration).ToList();
            var pending = batch.Jobs.Where(j => !j.Done && j.Attempt != null).ToList();
            if (pending.Count > 0 && pending.All(j => recorded.Any(i => j.ImageIncidentId != null ? i.Id == j.ImageIncidentId : i.SubStep == j.Name)))
                FlowIncidentStore.MarkRecorded(error, FlowTelemetry.StageAssets);
            return error;
        }

        internal static bool SafeImageRetry(AssetImageJob job)
        {
            return job.RetryCount < 2 && job.Attempt != null && (job.Attempt.State == "已暂停" || (job.Attempt.State == "未提交" && (job.Attempt.Detail ?? "").StartsWith("人工核对"))) && !FlowAutoRepair.IsUnrecoverable(new Exception(job.Attempt.Detail ?? ""));
        }

        internal static string ReviewKey(string root, AssetWork work)
        {
            foreach (var item in work.Items)
            {
                if (!HasImage(item)) throw new InvalidOperationException(item.Source.name + " 尚未完成资产图。");
                AssetProduction.Verify(root, item.Active);
            }
            return AgentData.Hash(work.AnalysisId + "\n" + String.Join("\n", work.Items.Select(i => i.Id + "/" + i.ActiveId + "/" + i.Active.Digest)));
        }

        private static void Deliver(string root, AssetWork work, bool manual)
        {
            var previous = AssetProduction.ReadDeliveryRecord(root, work);
            if (previous != null && previous.Items.Select(i => i.ActiveId).SequenceEqual(work.Items.Select(i => i.ActiveId))) return;
            foreach (var item in work.Items)
            {
                item.Active.State = "已通过"; item.Active.ReviewedUtc = AnalysisData.Now();
                item.Active.ReviewNote = manual ? "一键成片 · 人工审核通过" : "一键成片 · 自动校验交接";
            }
            AssetProduction.Deliver(root, work);
        }

        private const int TextConcurrency = 4;

        private static void FinishScenes(string root, string exe, string model, FilmBook book, List<FilmScene> scenes,
            CancellationToken token, Action<string> report, VideoModel target)
        {
            var boardSkill = Skill(root, LocalSkills.StageKey(2));
            var groupSkill = Skill(root, LocalSkills.StageKey(3));
            var promptSkill = Skill(root, LocalSkills.StageKey(4));
            if (book.V2Builtin && (boardSkill == null || promptSkill == null))
                throw new InvalidDataException("本项目缺少已绑定的竖屏分镜或最终提示词 Skill。请恢复项目 Skill 方案后重新继续。");
            lock (book) book.FinalSkill = promptSkill == null ? null : promptSkill.Reference;
            int lanesPerScene = Math.Max(1, TextConcurrency / scenes.Count);
            using (var workers = new SemaphoreSlim(TextConcurrency))
            {
                var ready = new List<FilmScene>();
                var failures = new List<Exception>();
                int grouped = 0, prompted = 0;
                var boards = scenes.Select(scene => WithWorker(workers,token,() =>
                {
                    Action<string> progress = msg => report(scene.Title + " · " + msg);
                    try
                    {
                        RunStep(root,model,scene,"分镜",3,token,() =>
                        {
                            if (scene.Shots.Count != 0) return;
                            var shots = FilmWriter.BoardForFlow(exe,model,boardSkill,book,scene,token,progress,target,root);
                            lock (book)
                            {
                                scene.Shots=shots; scene.Approved=scene.Grouped=false; scene.LastError=null;
                                scene.Status="分镜已生成"; scene.BoardSkill=boardSkill == null ? null : boardSkill.Reference.Name;
                                Film.Save(root,book);
                            }
                        });
                        RunStep(root,model,scene,"分段",3,token,() => GroupScene(root,exe,model,book,scene,groupSkill,token,progress,target));
                        lock (ready)
                        {
                            ready.Add(scene);
                            FlowTelemetry.UpdateStage(root,3,scene.Title + " 分镜分段完成",++grouped,scenes.Count);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { SaveSceneFailure(root,book,scene,failures,e); }
                })).ToArray();
                Task.WhenAll(boards).GetAwaiter().GetResult();
                if (failures.Count == 0) FlowTelemetry.CompleteStage(root,3,"全部场次分镜分段完成");
                else FlowTelemetry.FailStage(root,3,"部分场次待续做");
                if (ready.Count > 0) FlowTelemetry.EnterStage(root,4,"场级全局与单段画布提示词");
                var prompts = ready.Select(async scene =>
                {
                    Action<string> progress = msg => report(scene.Title + " · " + msg);
                    try
                    {
                        await WithWorker(workers,token,() => RunStep(root,model,scene,"场级全局",4,token,() =>
                        {
                            if (FilmWriter.EnsureSceneGlobalPromptForFlow(exe,model,promptSkill,book,scene,token,progress,root))
                                lock (book) Film.Save(root,book);
                        })).ConfigureAwait(false);
                        var missing = scene.Segments.Where(g => !FilmWriter.Current(book,scene,g)).ToArray();
                        int batchSize = Math.Max(1,Math.Min(4,(missing.Length+lanesPerScene-1)/lanesPerScene));
                        var jobs = missing.Select((g,i) => new {g,i}).GroupBy(x => x.i/batchSize).Select(batch =>
                        {
                            var segments=batch.Select(x => x.g).ToList();
                            string name="单段画布 " + (scene.Segments.IndexOf(segments[0])+1) + "–" + (scene.Segments.IndexOf(segments.Last())+1);
                            return WithWorker(workers,token,() => RunStep(root,model,scene,name,4,token,() =>
                            {
                                var replies=FilmWriter.WriteForFlow(exe,model,promptSkill,book,scene,segments,"",false,token,progress,root);
                                lock (book)
                                {
                                    for (int i=0;i<segments.Count;i++) FilmWriter.Accept(book,scene,segments[i],replies[i]);
                                    Film.Save(root,book);
                                }
                            }));
                        }).ToArray();
                        await Task.WhenAll(jobs).ConfigureAwait(false);
                        lock (ready) FlowTelemetry.UpdateStage(root,4,scene.Title + " 提示词完成",++prompted,scenes.Count);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { SaveSceneFailure(root,book,scene,failures,e); }
                }).ToArray();
                Task.WhenAll(prompts).GetAwaiter().GetResult();
                if (failures.Count > 0)
                {
                    var error=new InvalidDataException(String.Join("；",failures.Select(e => e.Message)));
                    if (failures.All(FlowIncidentStore.IsRecorded)) FlowIncidentStore.MarkRecorded(error,
                        failures[0].Data["flowStage"] is int ? (int)failures[0].Data["flowStage"] : FlowTelemetry.StageStoryboard);
                    throw error;
                }
            }
        }

        private static void SaveSceneFailure(string root, FilmBook book, FilmScene scene, List<Exception> failures, Exception e)
        {
            lock (book)
            {
                failures.Add(e);
                scene.LastError=FlowAutoRepair.Safe(e.Message);
                scene.Status="待续做";
                Film.Save(root,book);
            }
        }

        private static void RunStep(string root,string model,FilmScene scene,string step,int stage,CancellationToken token,Action action)
        {
            string key=scene.Id + "/" + step;
            string reason=null;
            var timer=new System.Diagnostics.Stopwatch();
            for (int trial=0;;trial++)
            {
                token.ThrowIfCancellationRequested();
                FlowTelemetry.UpsertWorkItem(root,key,stage,scene.Title,step,trial == 0 ? "运行中" : "重试中");
                try
                {
                    action();
                    if (trial > 0)
                        FlowIncidentStore.Record(root,new FlowIncident { Half=FlowHalf.PostAssets,StageIndex=stage,StageTitle=stage == 4 ? "最终提示词" : "分镜与分段",SubStep=scene.Title + " · " + step,
                            Category=FlowIncidentCategory.TransportAndTransient,SourceHint="FlowProduction.FinishScenes",RawErrorMessage=reason,
                            Summary="瞬时故障后恢复",ContextSnapshot=key,AutoRepairStrategy="退避重试",Resolved=true,ResolutionMs=timer.ElapsedMilliseconds,
                            AttemptsUsed=trial,Model=model,CodexFixAction="原步骤已通过校验",PermanentFixSuggestion="覆盖通道抖动与共享锁恢复回归。" });
                    FlowTelemetry.FinishWorkItem(root,key,step);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    token.ThrowIfCancellationRequested();
                    reason=FlowAutoRepair.Safe(e.Message);
                    if (trial < 2 && FlowAutoRepair.IsTransient(e))
                    {
                        timer.Start();
                        if (token.WaitHandle.WaitOne(500 << trial)) token.ThrowIfCancellationRequested();
                        continue;
                    }
                    FlowTelemetry.FinishWorkItem(root,key,reason,"失败");
                    if (FlowIncidentStore.IsRecorded(e)) throw;
                    FlowIncidentStore.Record(root,new FlowIncident { Half=FlowHalf.PostAssets,StageIndex=stage,StageTitle=stage == 4 ? "最终提示词" : "分镜与分段",SubStep=scene.Title + " · " + step,
                        Model=model, Category=FlowAutoRepair.IsTransient(e) ? FlowIncidentCategory.TransportAndTransient :
                            stage == 4 ? FlowIncidentCategory.FinalPromptValidation : step == "分段" ? FlowIncidentCategory.SegmentationConstraint : FlowIncidentCategory.StoryboardValidation,
                        SourceHint=stage == 4 ? "FilmWriter/VideoPromptFidelity" : step == "分段" ? "FlowProduction.GroupScene" : "FilmWriter.Board",
                        RawErrorMessage=reason,Summary=reason,ContextSnapshot=key,Resolved=false,AttemptsUsed=trial+1,
                        ResolutionMs=timer.ElapsedMilliseconds,AutoRepairStrategy="已保存其他场次，当前步骤待续做",PermanentFixSuggestion="按场次复现该校验点，补充边界输入回归。" });
                    FlowIncidentStore.MarkRecorded(e, stage);
                    throw;
                }
            }
        }

        private static async Task WithWorker(SemaphoreSlim workers,CancellationToken token,Action run)
        {
            await workers.WaitAsync(token).ConfigureAwait(false);
            try { await Task.Run(run,token).ConfigureAwait(false); }
            finally { workers.Release(); }
        }

        public sealed class SegmentChoice { public int first { get; set; } public int last { get; set; } }
        public sealed class SegmentReply { public List<SegmentChoice> segments { get; set; } }
        private const string SegmentSchema = @"{'type':'object','additionalProperties':false,'required':['segments'],'properties':{'segments':{'type':'array','items':{'type':'object','additionalProperties':false,'required':['first','last'],'properties':{'first':{'type':'integer'},'last':{'type':'integer'}}}}}}";

        private static void GroupScene(string root, string exe, string model, FilmBook book, FilmScene scene,
            SelectedSkill skill, CancellationToken token, Action<string> report, VideoModel target)
        {
            if (target == null && book.V2Builtin) target = new VideoModel { Min=1, Max=30, Images=9 };
            int limit=target==null ? 30 : Math.Min(30,target.Max);
            Film.RequireReady(root, book, scene);
            if (scene.Approved && scene.Grouped && ValidGroups(scene, scene.Segments,limit) &&
                (target == null || scene.Segments.All(g => Film.Shots(scene,g).Sum(sh => sh.seconds) >= target.Min &&
                    FilmWriter.References(book,scene,g).Select(r => r.Label).Distinct().Count() <= target.Images)) &&
                (!book.V2Builtin || !scene.Segments.Skip(1).Where((g,i) => CanMerge(book,scene,scene.Segments[i],g,limit,target)).Any())) return;
            report("正在编排片段");
            List<FilmSegment> groups;
            try
            {
                groups=Film.Group(scene,limit);
                if (target != null)
                {
                    // FitGroups may pad legacy shots. Keep provisional changes away from concurrent saves.
                    var preview = new FilmScene { Title=scene.Title,Cues=scene.Cues,
                        Shots=AgentData.Json().Deserialize<List<FilmShot>>(AgentData.Json().Serialize(scene.Shots)) };
                    VideoDelivery.FitGroups(book,preview,groups,target);
                }
            }
            catch (InvalidDataException error)
            {
                groups=RepairGroups(root,exe,model,book,scene,skill,token,report,target,limit,error.Message);
                return;
            }
            if (skill != null)
            {
                string request = "按当前分段 Skill、动作连贯性和节奏将本场分镜合并为若干视频片段。first/last 为从 1 开始的镜号，优先合并时空连续且时长合规的相邻短镜，避免连续零碎短段；mayCutAfter 为 false 的镜后禁止切段，双语及跨镜续接必须同段。连续且不重叠，顺序覆盖全部分镜，每段合计最多" + limit + "秒。"
                    + "\n分镜 " + AgentData.Json().Serialize(scene.Shots.Select((sh,i) => new { n=i+1, sh.seconds, sh.action, sh.end, sh.camera, sh.content, sh.continuation, mayCutAfter = !Film.ProtectedBoundary(scene,i) }));
                for (int trial = 0; ; trial++)
                {
                    SegmentReply reply=null;
                    string problem="分段顺序、保护边界或时长无效。";
                    try { reply=FilmWriter.Ask<SegmentReply>(exe,model,skill,SegmentSchema,request,token,report,retryInvalidOutput:false); }
                    catch (InvalidDataException invalid) { problem=invalid.Message; }
                    if (reply == null) reply=new SegmentReply();
                    groups = (reply.segments ?? new List<SegmentChoice>()).Where(g => g != null).Select(g => new FilmSegment { First=g.first-1, Last=g.last-1 }).ToList();
                    if (ValidGroups(scene, groups, limit)) break;
                    if (trial > 0)
                    {
                        var fix=FlowAutoRepair.ResolveStructuredJson(root,exe,model,skill,FlowHalf.PostAssets,3,"分段",scene.Title,
                            "FlowProduction.ValidGroups",SegmentSchema.Replace('\'','"'),AgentData.Json().Serialize(reply),problem,request,token,report);
                        if (fix.Succeeded)
                        {
                            try
                            {
                                var repaired=AgentData.ParseOutput<SegmentReply>(fix.RepairedText);
                                groups=(repaired.segments ?? new List<SegmentChoice>()).Where(g => g != null)
                                    .Select(g => new FilmSegment { First=g.first-1,Last=g.last-1 }).ToList();
                            }
                            catch (InvalidDataException) { groups.Clear(); }
                        }
                        if (!fix.Succeeded || !ValidGroups(scene,groups,limit))
                        {
                            groups=Film.Group(scene,limit);
                            fix=GroupFallback(problem,"按保护边界与时长重新分组。",fix.ElapsedMs);
                        }
                        FlowAutoRepair.RecordPost(root,model,3,scene.Title,"FlowProduction.ValidGroups",FlowIncidentCategory.SegmentationConstraint,problem,fix);
                        break;
                    }
                    request += "\n同一句的双语版本不可跨片段。" + String.Join("、", scene.Shots.Select((sh,i)=>Film.ProtectedBoundary(scene,i) ? "禁止在第"+(i+1)+"镜后切段" : null).Where(v=>v!=null)) + "上次分组未通过时长或顺序核验，重新计算后交付。";
                }
            }
            lock (book)
            {
                for (int i=1; i<groups.Count;)
                {
                    var left=groups[i-1];
                    var right=groups[i];
                    if (CanMerge(book,scene,left,right,limit,target))
                    {
                        groups[i-1]=new FilmSegment { First=left.First, Last=right.Last };
                        groups.RemoveAt(i);
                    }
                    else i++;
                }
                if (target!=null) groups=VideoDelivery.FitGroups(book,scene,groups,target);
                scene.Segments = groups.Select(g => scene.Segments.FirstOrDefault(old => old.First == g.First && old.Last == g.Last) ?? g).ToList(); scene.Approved = scene.Grouped = true;
                Film.Save(root, book);
            }
        }

        private static FlowRepairOutcome GroupFallback(string cause,string action,long ms)
        {
            return new FlowRepairOutcome { Succeeded=true,RootCause=cause,OneShotFix=action,ElapsedMs=ms,
                PermanentCodeFix="在分镜交付时预检连续对白组的时长与去重参考图预算，并用超限场次做回归。" };
        }

        public sealed class ShotBudget
        {
            public int seconds { get; set; }
            public List<string> assets { get; set; }
        }

        public sealed class BudgetReply { public List<ShotBudget> shots { get; set; } }

        private const string BudgetSchema=@"{""type"":""object"",""additionalProperties"":false,""properties"":{""shots"":{""type"":""array"",""items"":{""type"":""object"",""additionalProperties"":false,""properties"":{""seconds"":{""type"":""integer""},""assets"":{""type"":""array"",""items"":{""type"":""string""}}},""required"":[""seconds"",""assets""]}}},""required"":[""shots""]}";

        private static List<FilmSegment> RepairGroups(string root,string exe,string model,FilmBook book,FilmScene scene,
            SelectedSkill skill,CancellationToken token,Action<string> report,VideoModel target,int limit,string reason)
        {
            int images=target == null ? Int32.MaxValue : target.Images;
            // Validate on a private copy. Saving another scene cannot expose half-applied edits.
            var trial=AgentData.Json().Deserialize<FilmScene>(AgentData.Json().Serialize(scene));
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var pendingFixes=new List<FlowRepairOutcome>();
            for (int first=0;first<trial.Shots.Count;)
            {
                int last=first;
                while (Film.ProtectedBoundary(trial,last)) last++;
                var rows=trial.Shots.GetRange(first,last-first+1);
                var seg=new FilmSegment { First=first,Last=last };
                int total=rows.Sum(sh => sh.seconds);
                if (total > limit || FilmWriter.References(book,trial,seg).Select(r => r.Label).Distinct().Count() > images)
                {
                    if (rows.Count > limit) throw new InvalidDataException("连续保护组镜数超过秒数上限，无法保留每镜至少一秒。");
                    string facts=AgentData.Json().Serialize(rows.Select(sh => new { sh.code,sh.seconds,sh.assets,sh.action,sh.angle }));
                    var fix=FlowAutoRepair.ResolveStructuredJson(root,exe,model,skill,FlowHalf.PostAssets,3,"分段",scene.Title,
                        "Film.Group/VideoDelivery.FitGroups",BudgetSchema,facts,reason,
                        "仅重排此保护组的 seconds/assets。逐镜按原序、每镜至少1秒，不得延长或新增素材。总时长上限 " + limit +
                        " 秒，去重参考图上限 " + images + "。优先保留核心人物与场景，只精简次要背景。\n" + facts +
                        "\n资产：" + AgentData.Json().Serialize(rows.SelectMany(sh => FilmWriter.Assets(book,sh)).Distinct().Select(a => new { a.Id,a.Name,a.Category })),token,report);
                    var coreImages=rows.SelectMany(sh => FilmWriter.Assets(book,sh)
                        .Where(a => new[]{"人物","角色","场景","地点"}.Contains(a.Category)).Select(a => Film.Image(a,sh.angle).Digest))
                        .GroupBy(digest => digest).OrderByDescending(g => g.Count()).Take(images).Select(g => g.Key).ToArray();
                    var originals=rows.Select(sh => new ShotBudget { seconds=sh.seconds,assets=sh.assets.ToList() }).ToList();
                    bool accepted=false;
                    if (fix.Succeeded)
                    {
                        try
                        {
                            var plan=AgentData.ParseOutput<BudgetReply>(fix.RepairedText).shots;
                            if (plan != null && plan.Count == rows.Count && plan.Sum(p => p == null ? 0L : p.seconds) <= limit &&
                                plan.Select((p,i) => p != null && p.seconds >= 1 && p.seconds <= originals[i].seconds && p.assets != null &&
                                    p.assets.All(id => originals[i].assets.Contains(id)) &&
                                    (originals[i].assets.Count == 0 || p.assets.Count > 0)).All(ok => ok))
                            {
                                for (int i=0;i<rows.Count;i++) { rows[i].seconds=plan[i].seconds; rows[i].assets=plan[i].assets.Distinct().ToList(); }
                                var kept=FilmWriter.References(book,trial,seg).Select(r => r.Digest).Distinct().ToArray();
                                accepted=kept.Length <= images && coreImages.All(kept.Contains);
                            }
                        }
                        catch (InvalidDataException) { }
                    }
                    if (!accepted)
                    {
                        int previous=0, weight=0;
                        for (int i=0;i<rows.Count;i++)
                        {
                            weight+=originals[i].seconds-1;
                            int cumulative=total > limit ? (int)((long)weight*(limit-rows.Count)/(total-rows.Count)) : weight;
                            rows[i].seconds=1+cumulative-previous; previous=cumulative;
                            rows[i].assets=originals[i].assets;
                        }
                        var refs=rows.SelectMany(sh => FilmWriter.Assets(book,sh).Select(a => new { Asset=a,Image=Film.Image(a,sh.angle) }))
                            .Where(x => x.Image != null).GroupBy(x => x.Image.Digest).OrderByDescending(g =>
                                g.Any(x => new[]{"人物","角色","场景","地点"}.Contains(x.Asset.Category))).ThenByDescending(g => g.Count())
                            .Take(images).Select(g => g.Key).ToList();
                        foreach (var sh in rows) sh.assets=FilmWriter.Assets(book,sh).Where(a => refs.Contains(Film.Image(a,sh.angle).Digest)).Select(a => a.Id).ToList();
                        fix=GroupFallback(reason,"按比例压缩保护组，按核心类别与出场频次保留可绑定参考图。",timer.ElapsedMilliseconds);
                    }
                    for (int i=0;i<rows.Count;i++)
                        if (rows[i].seconds != originals[i].seconds && !String.IsNullOrEmpty(rows[i].content))
                            rows[i].content=System.Text.RegularExpressions.Regex.Replace(rows[i].content,@"^(\s*)\d+(?=\s*秒)","${1}"+rows[i].seconds);
                    pendingFixes.Add(fix);
                }
                first=last+1;
            }
            token.ThrowIfCancellationRequested();
            var groups=Film.Group(trial,limit);
            if (target != null) groups=VideoDelivery.FitGroups(book,trial,groups,target);
            Film.CheckShots(book,trial,trial.Shots);
            if (!ValidGroups(trial,groups,limit)) throw new InvalidDataException(reason);
            lock (book)
            {
                scene.Shots=trial.Shots;
                scene.Segments=groups;
                scene.Approved=scene.Grouped=true;
                Film.Save(root,book);
            }
            foreach (var fix in pendingFixes)
                FlowAutoRepair.RecordPost(root,model,3,scene.Title,"Film.Group/VideoDelivery.FitGroups",FlowIncidentCategory.SegmentationConstraint,reason,fix);
            return groups;
        }

        private static bool CanMerge(FilmBook book, FilmScene scene, FilmSegment left, FilmSegment right, int limit, VideoModel target)
        {
            int a = Film.Shots(scene,left).Sum(sh => sh.seconds), b = Film.Shots(scene,right).Sum(sh => sh.seconds);
            var joined = new FilmSegment { First = left.First, Last = right.Last };
            return Math.Min(a,b) <= 5 && a+b <= limit && (target == null ||
                FilmWriter.References(book,scene,joined).Select(r => r.Label).Distinct().Count() <= target.Images);
        }

        private static bool ValidGroups(FilmScene scene, IList<FilmSegment> groups,int limit)
        {
            int next = 0;
            foreach (var g in groups)
            {
                if (g.First != next || g.Last < g.First || g.Last >= scene.Shots.Count || Film.Shots(scene,g).Sum(sh => sh.seconds) > limit) return false;
                if (Film.ProtectedBoundary(scene, g.Last)) return false;
                next = g.Last + 1;
            }
            return groups.Count > 0 && next == scene.Shots.Count;
        }

    }
}
