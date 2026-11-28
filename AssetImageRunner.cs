using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private sealed class ImageResult
        {
            internal string Path;
            internal string Error;
            internal long DurationMs;
            internal bool TimedOut;
            internal bool MayHaveSubmitted;
            internal bool PlatformFailed;
            internal string SubmittedPrompt;
        }

        private static bool PendingImage(AssetItem item)
        {
            return item.Active == null || item.Active.State == "未提交" || item.Active.State == "已退回" ||
                item.Active.State == "已暂停" || item.Active.State == "需核对" || item.Active.State == "运行中";
        }

        private void GenerateAssets(string root, AnalysisVersion version, AnalysisResult analysis,
            string[] ids, AssetChannel channel)
        {
            if (ids == null || ids.Length == 0) return;
            TryAction(delegate
            {
                var work = AssetProduction.Load(root, version, analysis);
                string lane = "出图";
                if (AssetRunning(root, lane) || (work.ImageBatch != null && work.ImageBatch.NeedsAction))
                    lane = "出图 · " + ids[0] + (ids.Length > 1 ? " 等 " + ids.Length + " 项" : "");
                RunImageBatch(root, version, analysis, ids, channel, null, lane);
            });
        }

        private void RunImageBatch(string root, AnalysisVersion version, AnalysisResult analysis,
            string[] ids, AssetChannel chosen, string mode, string lane = "出图", string expectedBatch = null, string groupId = null)
        {
            if (mode == "恢复" && expectedBatch != null && !TryAction(delegate {
                var displayed = AssetProduction.Load(root, version, analysis).Batch(lane);
                if (displayed == null || displayed.Id != expectedBatch) throw new InvalidOperationException("出图批次已变化，请重新选择。");
                chosen = FlowChannels.Channel(displayed.ChannelId);
            })) return;
            Action<string, int, int> progress = (message, done, total) => { DevMonitor.Log(root,"MEDIA/OCR",message);SetAssetProgress(root, message, done, total, lane); };
            RunAsset(root, version, analysis, (work, cancel) => ProduceImageSelection(root, version, analysis, work, ids, chosen, mode,
                lane, codexStatus == null ? null : codexStatus.Path, null, progress, cancel, expectedBatch, groupId), localOnly: chosen != null, name: lane);
        }

        private Task<string> ProduceImages(string root, AnalysisVersion version, AnalysisResult analysis, AssetWork work,
            string[] ids, AssetChannel chosen, string mode, string lane, string codex, FlowConfig automatic,
            Action<string, int, int> progress, CancellationToken cancel)
        {
            return ProduceImageSelection(root, version, analysis, work, ids, chosen, mode, lane, codex, automatic, progress, cancel, null, null);
        }

        private async Task<string> ProduceImageSelection(string root, AnalysisVersion version, AnalysisResult analysis, AssetWork work,
            string[] ids, AssetChannel chosen, string mode, string lane, string codex, FlowConfig automatic,
            Action<string, int, int> progress, CancellationToken cancel, string expectedBatch, string groupId)
        {
            int confirmationPage = automatic == null ? pageRevision : -1;
            var options = AgentData.Options();
            var batch = work.Batch(lane);
            if (expectedBatch != null && (batch == null || batch.Id != expectedBatch || !batch.NeedsAction))
                throw new InvalidOperationException("出图批次已变化，请刷新后重新选择。");
            var targets = mode != null && batch != null ? batch.Jobs.Where(j => !j.IsBase).Select(j => j.ItemId).ToArray() : ids;
            using (AssetUsageLedger.Begin(root, version.Id, AssetUsageLedger.Image))
            using (var leases = new ImageWriteLeases())
            {
                var parents = AssetProduction.StateParents(work);
                var heldItems = new HashSet<string>(targets);
                foreach (string id in targets.Where(parents.ContainsKey)) heldItems.Add(parents[id].Id);
                if (mode != null && batch != null)
                    foreach (var job in batch.Jobs.Where(j => !String.IsNullOrEmpty(j.ParentItemId))) heldItems.Add(job.ParentItemId);
                leases.Claim(root, heldItems.Select(id => "资产图 · " + id));
                if (work.Batches().Any(p => p.Key != lane && p.Value.NeedsAction &&
                    p.Value.Jobs.Any(j => !j.IsBase && !j.Done && heldItems.Contains(j.ItemId))))
                    throw new InvalidOperationException("所选资产已在其它出图批次中，请查看原批次。");
                if (mode == null && batch != null && batch.NeedsAction)
                    throw new InvalidOperationException("该项目已有暂停的出图批次，请在原批次继续生成或核对原任务。");
                var channel = chosen;
                var requestedSize = automatic != null || assetTargetSize == null ? ImageOutputSettings.DefaultSize(chosen) : (assetTargetSize.Length == 0 ? null : assetTargetSize);
                SelectedSkill imageSkill = null;
                bool existing = batch != null && batch.NeedsAction;
                if (existing)
                {
                    if (mode == "恢复" && batch.ChannelId != "builtin")
                        channel = chosen != null && chosen.Id == batch.ChannelId ? chosen : FlowChannels.Channel(batch.ChannelId);
                    else if (mode == "恢复") channel = null;
                    await Task.Run(() => AssetProduction.ReconcileBatch(root, work, batch, cancel));
                    if (!batch.NeedsAction)
                    {
                        if (lane != "出图")
                        {
                            work.StoreBatch(lane, null);
                            AssetProduction.Save(root, work);
                        }
                        string recovered = "已找回上次出图的全部图片，请逐项审图。";
                        progress(recovered, batch.Jobs.Count, batch.Jobs.Count);
                        return recovered;
                    }
                    if (mode == "恢复" && !String.IsNullOrEmpty(batch.ChannelProfile) && AssetProduction.ChannelProfile(channel) != batch.ChannelProfile)
                        throw new InvalidOperationException("原批次的出图渠道设置已变更。恢复原设置，或选择「仅重试未完成项」使用当前渠道。");
                }
                bool responsesApi = FlowChannels.IsResponsesApi(channel);
                bool customApi = channel != null && ImageApiProfiles.IsProfile(channel.Id);
                bool externalCli = channel != null && !responsesApi && !customApi;
                bool prepareProviderPrompt = externalCli || ImageApiProfiles.IsImages(channel);
                if (responsesApi) CodexAuthMode.EnsureResponsesApi();
                else if (customApi) ImageApiProfiles.Key(channel);
                else if (channel == null) CodexAuthMode.EnsureBuiltinImage();
                if (!existing)
                {
                    var selected = work.Items.Where(i => ids.Contains(i.Id) &&
                        (PendingImage(i) || i.Active != null && (i.Active.State == "已通过" || automatic == null && ids.Length == 1 && i.Active.State == "待审图"))).ToList();
                    if (selected.Count == 0) throw new InvalidOperationException("没有可出图的资产。");
                    var blocked = selected.Where(i => !AssetProduction.PromptReady(i)).ToList();
                    if (blocked.Count > 0)
                    {
                        if (automatic == null && SameProject(root, projectFolder) && !exitAfterCodexTask)
                            ShowBlockedAssetPrompts(root, version, analysis);
                        return "有 " + blocked.Count + " 项提示词需要处理。";
                    }
                    bool directCli = externalCli && (AssetProduction.UsesDirectSpecs(root) ||
                        work.Items.All(i => i.PromptOrigin == AssetProduction.DirectPromptOrigin));
                    var imageChoice = directCli ? null : LocalSkills.Resolve(root, LocalSkills.Choice(root, ImageOutputSettings.SkillOperation));
                    if (imageChoice != null && imageChoice.Parts != null)
                    {
                        progress("正在检查图片 Skill 兼容性", 0, 0);
                        await Task.Run(() => SkillCompatibility.Ensure(codex, options.Model, imageChoice, cancel));
                    }
                    var script = AnalysisData.CurrentScript(root);
                    if (work.Groups == null)
                    {
                        progress("正在识别同一人物、场景和道具的不同状态", 0, 0);
                        await Task.Run(() => AssetProduction.EnsureGroups(root, version, analysis, work,
                            codex, options.Model, script, cancel));
                    }
                    batch = AssetProduction.NewBatch(root, work, script, selected, channel, requestedSize,
                        imageChoice == null ? null : imageChoice.Reference);
                    imageSkill = imageChoice;
                    foreach (var job in batch.Jobs.Where(j => !j.Done && j.Attempt != null))
                    {
                        try
                        {
                            await Task.Run(() => AssetProduction.TryRecoverImage(root, work, job.Attempt));
                            AssetProduction.ApplyImageJob(work, job);
                        }
                        catch (Exception e) { job.Attempt.Detail = AgentData.Short(e.Message, 260); }
                    }
                }

                var writableBases = batch.Jobs.Where(j => j.IsBase && (!j.Done || j.Attempt.State != "已通过")).Select(j => j.GroupId).ToArray();
                leases.Claim(root, writableBases.Select(id => "基础图 · " + id));
                if (writableBases.Length != 0 && AssetProduction.Load(root, version, analysis).Batches().Any(p => p.Key != lane && p.Value.NeedsAction && p.Value.Jobs.Any(j =>
                    j.IsBase && (!j.Done || j.Attempt.State != "已通过") && writableBases.Contains(j.GroupId))))
                    throw new InvalidOperationException("这项资产共用的基础图正在另一个批次中制作或等待审核。");
                var reusedParents = batch.Jobs.Where(j => !j.Done && !String.IsNullOrEmpty(j.ParentItemId) &&
                    (j.ExistingParent != null || batch.Jobs.Any(p => p.ItemId == j.ParentItemId && p.Done))).ToList();
                if (reusedParents.Count > 0)
                {
                    var latest = AssetProduction.Load(root, version, analysis);
                    foreach (var job in reusedParents)
                    {
                        var basis = AssetProduction.JobBasis(batch, job);
                        var parent = latest.Items.Single(i => i.Id == job.ParentItemId).Active;
                        if (basis == null || parent == null || parent.Id != basis.Id || parent.Digest != basis.Digest ||
                            (parent.State != "已通过" && parent.State != "待审图"))
                            throw new InvalidOperationException(job.Name + " 的基准图已变化，请重新建立出图批次。");
                        AssetProduction.Verify(root, parent);
                    }
                }
                if (existing) imageSkill = LocalSkills.Resolve(root, batch.ImageSkill);
                if (imageSkill != null && imageSkill.Reference.Digest != batch.ImageSkill.Digest)
                    throw new InvalidOperationException("本批图片 Skill 的内容已变化。恢复原 Skill，或取消本批后用当前 Skill 新建任务。");
                if (batch.TargetSize != null && batch.TargetSize != ImageOutputSettings.DefaultSize(channel) && ImageOutputSettings.ProviderSize(channel, batch.TargetSize) == null)
                    throw new InvalidOperationException("当前渠道没有配置本批规格。请选择支持该规格的渠道，或取消任务后重选规格。");
                if (existing && imageSkill != null && imageSkill.Parts != null)
                {
                    progress("正在检查图片 Skill 兼容性", 0, 0);
                    await Task.Run(() => SkillCompatibility.Ensure(codex, options.Model, imageSkill, cancel));
                }
                bool singleRetry = mode != null && mode.StartsWith("单项重试/", StringComparison.Ordinal);
                if (singleRetry)
                {
                    var target = AssetProduction.TimedOutJobs(root, batch).FirstOrDefault(j => mode == "单项重试/" + j.Attempt.Id);
                    if (target == null || mode != "单项重试/" + target.Attempt.Id)
                        throw new InvalidOperationException("所选超时任务已变化，请刷新项目。");
                    target.RetryCount = Math.Max(target.RetryCount + 1, automatic == null ? ImageOutputSettings.MaxRetryCount : 2);
                }
                bool restart = mode == "重新生成" || singleRetry;
                bool hasReferences = batch.ReferencePath != null || batch.Jobs.Any(j => !j.IsBase && (j.GroupId != null || !String.IsNullOrEmpty(j.ParentItemId)));
                if (externalCli && channel.Driver != FlowChannels.Dreamina && hasReferences && !(channel.Arguments ?? "").Contains("{reference}"))
                    throw new InvalidOperationException("这批包含参考图。为外部 CLI 加上平台支持的参考图参数及 {reference}，或选内置出图。");
                string globalReference = AssetProduction.BatchReference(root, batch);
                if (externalCli) options = channel.Options(options);
                int timeoutMinutes = ImageOutputSettings.TimeoutMinutes(Sys.ImageTimeoutMinutes);
                var toRun = batch.Jobs.Where(j => !j.Done && (singleRetry
                    ? j.Attempt != null && mode == "单项重试/" + j.Attempt.Id
                    : restart || j.Attempt == null)).ToList();
                bool manualResume = existing && mode == "恢复" && automatic == null;
                if (manualResume)
                    toRun = AssetProduction.ReadyJobs(batch).Where(j => groupId == null ||
                        !j.IsBase && j.GroupId == groupId && ids.Contains(j.ItemId)).ToList();
                if (toRun.Count == 0)
                {
                    work.StoreBatch(lane, batch);
                    AssetProduction.Save(root, work);
                    return AssetProduction.PendingSummary(batch);
                }
                await Task.Run(() => AgentMedia.Budget(options, "图片", toRun.Count));
                if (exitAfterCodexTask || cancel.IsCancellationRequested) return "出图已暂停";
                string name = channel == null ? CodexAuthMode.Label + " 内置图片生成" : channel.Name;
                string route = externalCli ? "cli" : channel == null ? "builtin" : channel.Id;
                var cost = AgentMedia.Cost(options, "图片");
                var baseNames = toRun.Where(j => j.IsBase).Select(j => j.Name).ToList();
                string risk = toRun.Any(j => j.Attempt != null)
                    ? "\n上次未取得结果的任务会重新提交，可能产生新的用量。" : "";
                bool approvedContinuation = existing && !restart && groupId == null && toRun.All(j => !String.IsNullOrWhiteSpace(j.ApprovedPrompt))
                    && batch.ChannelProfile == AssetProduction.ChannelProfile(channel);
                bool autoBuiltin = approvedContinuation ? batch.AllowBuiltinTimeoutRetry : Sys.AllowBuiltinTimeoutRetry;
                int prepared = 0;
                await Task.Run(() => Parallel.ForEach(toRun, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = 4 }, job =>
                {
                    if ((restart && batch.ChannelProfile != AssetProduction.ChannelProfile(channel)) || String.IsNullOrWhiteSpace(job.ApprovedPrompt))
                    {
                        string prompt = ReferencePrompt(batch, job, route);
                        prompt = ImageOutputSettings.ApplySize(prompt, batch.TargetSize);
                        if (prepareProviderPrompt && imageSkill != null && !job.DirectSpec)
                        {
                            string direction = AssetProduction.CodexText(codex, options.Model,
                                "为以下图片请求按已选 Skill 编写补充指导。保留对象、事实、规格、参考图和版式。仅交付文字。\n" + prompt,
                                cancel, skill: imageSkill);
                            if (String.IsNullOrWhiteSpace(direction) || direction.Length > 24000)
                                throw new InvalidDataException("图片 Skill 未返回有效指导，请核对所选 Skill。");
                            prompt += "\n图片 Skill 补充指导\n" + direction.Trim();
                        }
                        job.ApprovedPrompt = prompt;
                    }
                    if (prepareProviderPrompt && imageSkill != null && !job.DirectSpec)
                    {
                        int doneCount = Interlocked.Increment(ref prepared);
                        Window.Dispatcher.BeginInvoke(new Action(() => progress("并行准备渠道提示词", doneCount, toRun.Count)));
                    }
                }));
                cancel.ThrowIfCancellationRequested();
                if (exitAfterCodexTask) return "出图已暂停";
                string preview = String.Join("\n\n", toRun.Select(j => "【" + j.Name + "】\n" + j.ApprovedPrompt));
                string answer;
                if (automatic == null && !approvedContinuation && await ProjectDecision(Path.GetFileName(root), "确认资产出图",
                    () => confirmationPage == pageRevision,
                    () => { ShowAssetPage(assetDetailId); confirmationPage = pageRevision; }, () => Prompt(restart ? "重试未完成的资产图" : "确认资产出图",
                    "项目 · " + Path.GetFileName(root) + "\n渠道 · " + name +
                    "\n规格 · " + ImageOutputSettings.Label(batch.TargetSize) +
                    "\n图片 Skill · " + (imageSkill == null ? "无" : imageSkill.Reference.Name) +
                    "\n将提交 " + toRun.Count + " 张，其中 " + baseNames.Count + " 张基础图。" +
                    "\n单张时限 " + timeoutMinutes + " 分钟。提交前超时最多自动重试一次。" +
                    (route == "builtin" && !CodexAuthMode.IsApi && autoBuiltin ? "\n已开启内置超时重提。无可验证回图时，每项最多额外提交一次，可能增加用量。" :
                        "\n已启动的任务超时会先查原图，保留不确定项待核对。") +
                    (baseNames.Count == 0 ? "" : "\n基础图 · " + String.Join("、", baseNames)) +
                    "\n估算总额 · " + (cost.HasValue ? (cost.Value * toRun.Count).ToString("0.##", CultureInfo.InvariantCulture) + " 元" : "未知") +
                    (restart ? "\n仅提交未完成项，已出图的资产和已采用的基础图均保留。" : "") + risk,
                    "确认出图", "取消", false, out answer, preview: preview) ? 1 : -1, cancel) != 1) return "已取消出图提交";
                cancel.ThrowIfCancellationRequested();
                foreach (var job in toRun)
                {
                    if (mode == "重新生成" && automatic != null && job.Attempt != null)
                    {
                        if (!FlowProduction.SafeImageRetry(job)) throw new InvalidOperationException("原任务需核对，已停止自动重提。");
                        job.RetryCount++;
                    }
                    AssetProduction.ResetJob(work, job);
                }
                batch.ChannelId = channel == null ? "builtin" : channel.Id;
                batch.AllowBuiltinTimeoutRetry = route == "builtin" && !CodexAuthMode.IsApi && autoBuiltin;
                batch.ChannelName = name;
                batch.ChannelProfile = AssetProduction.ChannelProfile(channel);
                work.StoreBatch(lane, batch);
                AssetProduction.Save(root, work);
                string result = await ExecuteImageBatch(root, version, work, batch, options, codex, route, globalReference, !externalCli ? batch.TargetSize : ImageOutputSettings.ProviderSize(channel, batch.TargetSize), imageSkill, TimeSpan.FromMinutes(timeoutMinutes), automatic != null, cancel, progress, singleRetry || manualResume ? toRun : null);
                if (lane != "出图" && !batch.NeedsAction)
                {
                    work.StoreBatch(lane, null);
                    AssetProduction.Save(root, work);
                }
                return result;
            }
        }

        private static string ImageWaitDetail(int seconds, string phase)
        {
            return "等待回图 " + seconds + " 秒" + (String.IsNullOrWhiteSpace(phase) ? "" : " · " + phase);
        }

        private static string ImageWorkKey(AssetImageJob job)
        {
            return "img-" + (job.ItemId ?? job.GroupId);
        }

        private static FlowIncident ImageIncident(AssetImageJob job, ImageResult result, string strategy)
        {
            if (job.PendingImageIncident == null)
            {
                bool builtin = job.Attempt != null && FlowChannels.IsNative(job.Attempt.PlatformId);
                string attemptId = job.Attempt == null ? "" : (job.Attempt.Id ?? "");
                job.PendingImageIncident = new FlowIncident { Id = job.ImageIncidentId, Half = FlowHalf.PreAssets, StageIndex = FlowTelemetry.StageAssets,
                    StageTitle = "资产出图", Category = FlowIncidentCategory.AssetImageGeneration, SubStep = job.Name,
                    ErrorCode = attemptId, Summary = job.Name + " 出图失败", RawErrorMessage = result.Error,
                    ContextSnapshot = (attemptId.Length == 0 ? "" : "任务回执 ID：" + attemptId + "\n") + job.ApprovedPrompt,
                    SourceHint = builtin ? "OpenAiResponsesClient.cs / AssetImageRunner.cs" : "AssetImageRunner.cs / 外部出图渠道",
                    PermanentFixSuggestion = "核对出图提示词、渠道参数及原任务回执。" };
            }
            job.PendingImageIncident.AutoRepairStrategy = strategy;
            return job.PendingImageIncident;
        }

        private static void FinishImageIncident(string root, AssetImageJob job, bool resolved)
        {
            if (job.PendingImageIncident == null) return;
            job.PendingImageIncident.Resolved = resolved;
            job.ImageIncidentId = FlowIncidentStore.Record(root, job.PendingImageIncident).Id;
            job.PendingImageIncident = null;
        }

        private static async Task RepairImageSpec(string root, string codex, AgentOptions options, AssetImageJob completed,
            string error, string targetSize, CancellationToken cancel, Action<string> report)
        {
            var incident = completed.PendingImageIncident;
            try
            {
                var fix = await Task.Run(() => FlowAutoRepair.ResolveImagePrompt(root, codex, options.Model,
                    completed.Name, completed.Category, completed.ApprovedPrompt, error, cancel, report));
                incident.CodexDiagnosis = fix.RootCause;
                incident.CodexFixAction = fix.OneShotFix;
                if (!String.IsNullOrWhiteSpace(fix.PermanentCodeFix)) incident.PermanentFixSuggestion = fix.PermanentCodeFix;
                incident.ResolutionMs += fix.ElapsedMs;
                if (fix.Succeeded && !String.IsNullOrWhiteSpace(fix.RepairedText))
                {
                    completed.ApprovedPrompt = ImageOutputSettings.ApplySize(fix.RepairedText, targetSize);
                    incident.CodexFixAction += "\n修订后提示词：" + completed.ApprovedPrompt;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                incident.CodexFixAction = "文本通道未能返回修订提示词，沿用已确认提示词：" + e.Message;
                report(incident.CodexFixAction);
            }
        }

        private async Task<bool> RecoverPaidImage(string root, AssetWork work, AssetImageJob job, CancellationToken cancel,
            Action<string> report)
        {
            for (int i = 0; i < 2; i++)
            {
                if (exitAfterCodexTask || cancel.IsCancellationRequested) break;
                report("原任务受理状态待核对，第 " + (i+1) + "/2 次补查");
                try
                {
                    await Task.Delay(i == 0 ? 3000 : 6000, cancel);
                    job.PendingImageIncident.AttemptsUsed++;
                    if (await Task.Run(() => AssetProduction.TryRecoverImageWithCancellation(root, work, job.Attempt, cancel))) return true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    job.PendingImageIncident.CodexFixAction = "原任务补查未取回：" + e.Message;
                    if (AgentMedia.ConfirmedFailure(AgentMedia.JobFolder(root, new AgentJob { Id = job.Attempt.Id }))) break;
                }
            }
            return false;
        }

        private async Task<string> ExecuteImageBatch(string root, AnalysisVersion version, AssetWork work,
            AssetImageBatch batch, AgentOptions options, string codex, string route, string reference, string providerSize, SelectedSkill imageSkill, TimeSpan timeout, bool acceptBases, CancellationToken cancel, Action<string, int, int> progress, IList<AssetImageJob> allowed)
        {
            int total = batch.Jobs.Count;
            var active = new Dictionary<Task<ImageResult>, AssetImageJob>();
            string firstError = null;
            bool stopScheduling = false;
            var clocks = new Dictionary<string, System.Diagnostics.Stopwatch>();
            Action<AssetImageJob,string,string> track = (j, message, state) =>
                FlowTelemetry.UpsertWorkItem(root, ImageWorkKey(j), FlowTelemetry.StageAssets, j.Name, message,
                    state, batch.Jobs.Count(x => x.Done), total);
            progress("检查图片渠道", batch.Jobs.Count(j => j.Done), total);
            await Task.Run(() =>
            {
                if (ImageApiProfiles.IsProfile(route)) ImageApiProfiles.Key(ImageApiProfiles.Get(route));
                else if (FlowChannels.IsResponsesApi(route)) AssetChannels.CheckResponsesApi();
                else if (route == "builtin") AssetChannels.CheckBuiltin(codex, cancel);
                else AssetChannels.CheckCli(options, Path.GetDirectoryName(options.ImageCli), cancel);
            });
            while (true)
            {
                if (!cancel.IsCancellationRequested && !exitAfterCodexTask && !stopScheduling)
                {
                    try
                    {
                        var ready = AssetProduction.ReadyJobs(batch).Where(j => allowed == null || allowed.Contains(j)).Take(ImageOutputSettings.ImageConcurrency - active.Count).ToList();
                        if (ready.Count > 0)
                        {
                            AssetProduction.RequireCurrent(root, version.Id);
                            foreach (var retry in ready.Where(j => j.RetrySource != null).ToList())
                            {
                                if (!await Task.Run(() => AssetProduction.TryRecoverImage(root, work, retry.RetrySource))) continue;
                                retry.Attempt = retry.RetrySource;
                                retry.RetrySource = null;
                                AssetProduction.ApplyImageJob(work, retry);
                                if (acceptBases && retry.IsBase && retry.Attempt.State == "待审图")
                                    AssetProduction.AcceptFlowBase(root, work, batch, retry);
                                FinishImageIncident(root, retry, true);
                                FlowTelemetry.FinishWorkItem(root, ImageWorkKey(retry), "原任务已回图");
                                FlowTelemetry.UpdateStage(root, FlowTelemetry.StageAssets, retry.Name + " 已回图", batch.Jobs.Count(j => j.Done), total);
                                AssetProduction.Save(root, work);
                                ready.Remove(retry);
                            }
                            if (ready.Count == 0) continue;
                            var inputs = ready.Select(job => new { Job = job, References = ImageReferences(root, batch, job, reference, route) }).ToList();
                            foreach (var input in inputs)
                            {
                                var job = input.Job;
                                var basis = AssetProduction.JobBasis(batch, job);
                                job.Attempt = new AssetAttempt
                                {
                                    Id = AgentData.Id(), State = "运行中", Prompt = job.ApprovedPrompt,
                                    PromptLines = job.Lines, Style = job.Style, Channel = batch.ChannelName,
                                    PlatformId = batch.ChannelId, CreatedUtc = AnalysisData.Now(),
                                    ReferencePath = basis == null ? batch.ReferencePath : basis.Path,
                                    ReferenceDigest = basis == null ? batch.ReferenceDigest : basis.Digest,
                                    BaseImagePath = basis == null ? null : basis.Path,
                                    BaseImageDigest = basis == null ? null : basis.Digest,
                                    TargetSize = batch.TargetSize, ImageSkill = batch.ImageSkill,
                                    PromptSkill = job.PromptSkill, BaseRevision = job.BaseRevision
                                };
                                if (job.RetrySource != null) AssetUsageLedger.Retry(job.RetrySource.TimedOut ? "出图超时后重试" : "续做未完成出图");
                                job.RetrySource = null;
                                AssetProduction.ApplyImageJob(work, job);
                            }
                            AssetProduction.Save(root, work);
                            foreach (var input in inputs)
                            {
                                var job = input.Job;
                                var attempt = job.Attempt;
                                track(job, "正在生成横屏 4:3 资产图", "运行中");
                                clocks[attempt.Id] = System.Diagnostics.Stopwatch.StartNew();
                                active.Add(Task.Run(() => PerformImage(root, route, options, codex, attempt, input.References, providerSize, imageSkill, (int)timeout.TotalMilliseconds, cancel)), job);
                            }
                        }
                    }
                    catch (Exception e) { stopScheduling = true; firstError = AgentData.Short(e.Message, 360); }
                }
                if (active.Count == 0) break;
                var oldest = active.Values.OrderByDescending(j => clocks[j.Attempt.Id].ElapsedMilliseconds).First();
                var waiting = clocks[oldest.Attempt.Id].Elapsed;
                string graceNote = route == "builtin" && !CodexAuthMode.IsApi && waiting > timeout
                    ? "（已进入额外 " + (ImageOutputSettings.ReturnGraceMs / 60000) + " 分钟收图等待）"
                    : "";
                string activity = "并行出图 " + active.Count + " 项 · " + oldest.Name + " 已等待 " +
                    ((int)waiting.TotalMinutes) + "分" + waiting.Seconds + "秒 / " + (int)timeout.TotalMinutes + "分钟" + graceNote + " · 自动检查回图中";
                foreach (var running in active.Values)
                {
                    string receiptFolder = AgentMedia.JobFolder(root, new AgentJob { Id = running.Attempt.Id });
                    string phase = FlowChannels.IsNative(route) ? ImageRunTrace.Last(receiptFolder) : null;
                    track(running, ImageWaitDetail((int)clocks[running.Attempt.Id].Elapsed.TotalSeconds, phase), "运行中");
                    if (running == oldest && !String.IsNullOrWhiteSpace(phase)) activity += " · " + phase;
                }
                progress(cancel.IsCancellationRequested ? "正在停止并保存已完成图片" : activity,
                    batch.Jobs.Count(j => j.Done), total);
                var signal = await Task.WhenAny(active.Keys.Cast<Task>().Concat(new Task[] { Task.Delay(1000) }));
                var finished = signal as Task<ImageResult>;
                if (finished == null || !active.ContainsKey(finished)) continue;
                var completed = active[finished];
                clocks.Remove(completed.Attempt.Id);
                active.Remove(finished);
                var result = await finished ?? new ImageResult { Error = "未收到出图回执", MayHaveSubmitted = true };
                completed.Attempt.DurationMs = result.DurationMs;
                completed.Attempt.TimedOut = result.TimedOut;
                if (result.SubmittedPrompt != null) completed.Attempt.Prompt = result.SubmittedPrompt;
                try
                {
                    if (result.Path != null)
                        await Task.Run(() => AssetProduction.Attach(root, work, completed.Attempt,
                            AgentData.Inside(AgentData.DirectoryFor(root), result.Path)));
                    else
                    {
                        bool uncertain = result.MayHaveSubmitted && !result.PlatformFailed;
                        bool paidCheck = acceptBases && route == "cli" && uncertain;
                        completed.Attempt.State = uncertain ? "需核对" : "已暂停";
                        completed.Attempt.Detail = result.Error;
                        if (acceptBases) ImageIncident(completed, result, paidCheck ? "只补查原任务，避免重复扣费" : "检查原任务回图");
                        bool recovered = paidCheck
                            ? await RecoverPaidImage(root, work, completed, cancel, msg => track(completed, msg, "核对中"))
                            : await Task.Run(() => AssetProduction.TryRecoverImage(root, work, completed.Attempt));
                        if (!recovered)
                        {
                            if (paidCheck && AgentMedia.ConfirmedFailure(AgentMedia.JobFolder(root, new AgentJob { Id = completed.Attempt.Id })))
                            {
                                result.PlatformFailed = true;
                                uncertain = false;
                                completed.Attempt.State = "已暂停";
                            }
                            bool empty = !acceptBases && route == "builtin" && !CodexAuthMode.IsApi && batch.AllowBuiltinTimeoutRetry &&
                                AssetProduction.EmptyBuiltinSession(root, completed.Attempt);
                            bool retryable = acceptBases ? !uncertain : result.TimedOut && (!result.MayHaveSubmitted || empty);
                            if (retryable && (!acceptBases || !FlowAutoRepair.IsUnrecoverable(new Exception(result.Error))) && !cancel.IsCancellationRequested && !exitAfterCodexTask &&
                                completed.RetryCount < (acceptBases ? 2 : ImageOutputSettings.MaxRetryCount))
                            {
                                if (acceptBases)
                                {
                                    var incident = ImageIncident(completed, result, "已核对原任务，安全重试");
                                    incident.AttemptsUsed++;
                                    if (!result.TimedOut && (!String.IsNullOrEmpty(codex) || CodexAuthMode.IsApi))
                                    {
                                        track(completed, result.Error, "自动修复中");
                                        int done = batch.Jobs.Count(j => j.Done);
                                        await RepairImageSpec(root, codex, options, completed, result.Error, batch.TargetSize,
                                            cancel, msg => progress(msg, done, total));
                                    }
                                }
                                cancel.ThrowIfCancellationRequested();
                                completed.RetryCount++;
                                completed.Attempt.Detail = result.Error + " · 已安排安全重试。";
                                AssetProduction.ResetJob(work, completed);
                                progress("准备重试 · " + completed.Name, batch.Jobs.Count(j => j.Done), total);
                                track(completed, "准备重试", "重试中");
                            }
                            else
                            {
                                if (uncertain) completed.Attempt.Detail += " 已检查原任务，重新提交前请核对。";
                                if (firstError == null) firstError = completed.Name + " · " + completed.Attempt.Detail;
                            }
                        }
                    }
                    AssetProduction.ApplyImageJob(work, completed);
                    if (completed.Done) AssetUsageLedger.Success(1);
                    if (acceptBases && completed.IsBase && completed.Done && completed.Attempt.State == "待审图")
                        AssetProduction.AcceptFlowBase(root, work, batch, completed);
                    if (completed.Attempt != null) FinishImageIncident(root, completed, completed.Done);
                    AssetProduction.Save(root, work);
                    if (completed.Done && !acceptBases && FlowCreation.IsFlowProject(root))
                        FlowIncidentStore.ResolveLatestUnresolved(root, FlowHalf.PreAssets, FlowTelemetry.StageAssets,
                            completed.Name, "已在资产页手动重新生成并取回图片。", 0, completed.RetryCount);
                }
                catch (Exception e)
                {
                    if (acceptBases && completed.PendingImageIncident == null && completed.Attempt != null)
                        ImageIncident(completed, new ImageResult { Error = e.Message }, "核对出图与保存结果");
                    if (firstError == null) firstError = completed.Name + " · " + AgentData.Short(e.Message, 330);
                    if (!completed.Done && completed.Attempt != null)
                    {
                        completed.Attempt.State = (result == null || (result.Path != null || result.MayHaveSubmitted) && !result.PlatformFailed) ? "需核对" : "已暂停";
                        completed.Attempt.Detail = AgentData.Short(e.Message, 330);
                        AssetProduction.ApplyImageJob(work, completed);
                    }
                    FinishImageIncident(root, completed, false);
                    try { AssetProduction.Save(root, work); }
                    catch (Exception write) { exitAfterCodexTask = false; stopScheduling = true; firstError = "保存进度失败 · " + write.Message; }
                }
                if (completed.Attempt != null)
                {
                    string state = completed.Done ? "已完成" : completed.Attempt.State;
                    FlowTelemetry.FinishWorkItem(root, ImageWorkKey(completed), completed.Done ? "已取回图片" : completed.Attempt.Detail, state);
                    FlowTelemetry.UpdateStage(root, FlowTelemetry.StageAssets, completed.Name + " · " + state, batch.Jobs.Count(j => j.Done), total);
                }
            }

            int count = batch.Jobs.Count(j => j.Done);
            string summary = AssetProduction.PendingSummary(batch);
            if (firstError != null && (stopScheduling || !batch.Jobs.Any(j => !j.Done && j.Attempt != null))) summary = firstError + "\n" + summary;
            progress(summary, count, total);
            return summary;
        }

        private static string ReferencePrompt(AssetImageJob job)
        {
            return ReferencePrompt(null, job, "builtin");
        }

        private static string ReferencePrompt(AssetImageBatch batch, AssetImageJob job, string route)
        {
            bool hasParent = !job.IsBase && !String.IsNullOrEmpty(job.ParentItemId);
            bool hasFace = !job.IsBase && job.GroupId != null &&
                (batch == null || batch.Jobs.Any(j => j.IsBase && j.GroupId == job.GroupId));
            bool builtin = FlowChannels.IsNative(route);
            bool hasGlobalStyle = batch != null && !String.IsNullOrWhiteSpace(batch.ReferencePath) && (builtin || (!hasParent && !hasFace));
            bool structure = job.Category == "场景" || job.Category == "道具";
            var parts = new List<string>();
            if (hasParent)
            {
                parts.Add(structure
                    ? "第一张附图是此场景/道具的基准成图。严格保持同一空间布局/器物固定结构、材质、比例与无黑边满幅构图，仅按要求表现本状态的光线、开合、破损或污渍变化。"
                    : "第一张附图是此角色穿着同一套服装的基准成图。严格保持同一人物面容、发型、五视图版式以及完全相同的服装款式、剪裁、面料、配色与配饰，仅按要求叠加本状态的破损、污渍、淋湿或穿脱变化，严禁更换服装款式或产生黑边灰边。");
                if (builtin && hasFace)
                    parts.Add(structure
                        ? "第二张附图是共用基础图，仅核对空间布局或器物固定结构，严禁覆盖本项状态。"
                        : "第二张附图是该角色共用的身份基础图，仅用于辅助核对面部骨相、五官与发际线一致性，严禁沿用第二张附图的服装或把本项状态改回干净/干燥态。");
            }
            else if (hasFace)
                parts.Add(structure
                    ? "第一张附图是此场景/道具的共用基础图。严格保持同一空间布局/器物固定结构与材质基准，按下文要求绘制本分项状态。"
                    : "第一张附图是此角色共用的身份基础图，仅用于锁定同一人物的面部骨相、五官、发际线与体型，严禁照抄第一张附图的服装；请严格按下文新服装与五视图版式绘制本套造型。");
            if (hasGlobalStyle)
                parts.Add("第" + "一二三"[parts.Count] + "张附图仅作为全片整体画风、影调与渲染质感参考，严禁带入其中的人物、构图、画框黑边或文字。");
            parts.Add(job.Prompt);
            return String.Join("\n", parts);
        }

        private static List<string> ImageReferences(string root, AssetImageBatch batch, AssetImageJob job,
            string reference, string route)
        {
            var pictures = new List<string>();
            var basis = AssetProduction.JobBasis(batch, job);
            if (basis != null) pictures.Add(AssetProduction.Verify(root, basis));
            if (!String.IsNullOrEmpty(job.ParentItemId))
            {
                if (basis == null) throw new InvalidOperationException(job.Name + " 缺少基准态成图。");
                var face = batch.Jobs.FirstOrDefault(j => j.IsBase && j.GroupId == job.GroupId && j.Done && j.Attempt.State == "已通过");
                if (FlowChannels.IsNative(route) && face != null) pictures.Add(AssetProduction.Verify(root, face.Attempt));
            }
            if (reference != null && (FlowChannels.IsNative(route) || pictures.Count == 0)) pictures.Add(reference);
            return pictures;
        }

        private static readonly SemaphoreSlim imageSlots = new SemaphoreSlim(ImageOutputSettings.ImageConcurrency);

        private static ImageResult PerformImage(string root, string route, AgentOptions options, string codex,
            AssetAttempt attempt, IList<string> references, string providerSize, SelectedSkill imageSkill, int timeoutMs, CancellationToken cancel)
        {
            var job = new AgentJob { Id = attempt.Id, Prompt = attempt.Prompt };
            var folder = AgentMedia.JobFolder(root, job);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            bool entered = false;
            try
            {
                imageSlots.Wait(cancel);
                entered = true;
                timer.Restart();
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, AgentMedia.SubmittedPromptFile), job.Prompt);
                string path = RunMedia(codex, root, folder, folder, new AgentBatch { Kind = "图片", Route = route }, job,
                    null, false, options, references, cancel, true, providerSize, imageSkill, timeoutMs);
                return new ImageResult { Path = path, DurationMs = timer.ElapsedMilliseconds, SubmittedPrompt = job.Prompt };
            }
            catch (Exception e)
            {
                string probe = Path.Combine(folder, AgentMedia.PreflightFailureFile);
                string reason = e.Message;
                try
                {
                    if (File.Exists(probe)) reason = File.ReadAllText(probe);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                bool timedOut = e is TimeoutException;
                string phase = ImageRunTrace.Last(folder);
                return new ImageResult {
                    Error = timedOut ? "单张图片生成超时。" + (phase == null ? "" : " 最后记录：" + phase) : AgentData.Short(reason, 400),
                    DurationMs = timer.ElapsedMilliseconds, TimedOut = timedOut, PlatformFailed = AgentMedia.ConfirmedFailure(folder),
                    MayHaveSubmitted = File.Exists(Path.Combine(folder, "invoked")), SubmittedPrompt = job.Prompt
                };
            }
            finally { if (entered) imageSlots.Release(); }
        }
    }
}
