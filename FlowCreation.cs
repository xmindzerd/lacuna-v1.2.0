using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneClickFilm
{
    public sealed class FlowDraftDecision
    {
        public string Stage, Selected, Excluded, Point, Reason;
        public bool ByCodex;
    }

    public sealed class FlowDraft
    {
        public VideoOptions Video = new VideoOptions { Review = true };
        public string Language = "中文", Name, Script, Ratio = "9:16", Channel = "builtin";
        public bool Manual = true, DefaultSkills;
        public string FinalPromptSkillMode = "builtin", FinalPromptSkillFile, BuiltinPromptSkillId = BuiltinSkills.DefaultFinalPromptId;
        public bool DefaultCustomPromptSkill;
        public List<string> Files = new List<string>();
        public List<FlowDraftDecision> Decisions = new List<FlowDraftDecision>();
    }

    public sealed class FlowConfig
    {
        public string Ratio, Channel, ChannelName, Combination, ScriptHash;
        public VideoOptions Video = new VideoOptions();
        public string AssetRatio = ImageOutputSettings.DefaultAssetRatio;
        public bool Manual, Reused;
        public string PipelineMode, FinalPromptSkillMode, FinalPromptSkillName;
        internal bool IsV2Builtin { get { return PipelineMode == "v2-builtin"; } }
    }

    public sealed class FlowState
    {
        public string PromptKey, PendingKey, ChargeKey, ConfirmedCredits;
        public string Status = "创建成功", Phase, Detail, Utc, ReviewKey, Export, FilmRevision;
    }

    internal static class FlowCreation
    {
        internal static bool Development { get { return false; } }
        internal const string StateFile = @"一键成片\制作进度.json";
        internal const string ConfigFile = @"一键成片\项目配置.json";
        internal static string DraftFile { get { return Path.Combine(AgentData.Local, "一键成片创建草稿.json"); } }
        private static string DefaultsFile { get { return Path.Combine(AgentData.Local, "一键成片默认最终提示词.json"); } }

        internal static FlowDraft NewDraft(string path = null)
        {
            FlowDraft draft = null;
            try { draft = AgentData.Read<FlowDraft>(path ?? DraftFile); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (draft != null)
            {
                draft.Ratio = "9:16";
                if (String.IsNullOrWhiteSpace(draft.FinalPromptSkillMode)) draft.FinalPromptSkillMode = "builtin";
                draft.BuiltinPromptSkillId = BuiltinSkills.DefaultFinalPromptId;
                if (draft.Files == null) draft.Files = new List<string>();
                if (draft.Decisions == null) draft.Decisions = new List<FlowDraftDecision>();
                if (draft.Video == null) draft.Video = new VideoOptions { Review = true };
                return draft;
            }
            var fresh = new FlowDraft();
            try { fresh.FinalPromptSkillFile = AgentData.Read<string>(DefaultsFile); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (File.Exists(fresh.FinalPromptSkillFile))
            {
                fresh.FinalPromptSkillMode = "custom";
                fresh.DefaultCustomPromptSkill = true;
            }
            return fresh;
        }

        internal const string RegularConfig = @"影片制作\视频设置.json";
        internal static bool IsFlowProject(string root) { return File.Exists(AgentData.Inside(root,ConfigFile)); }
        internal static bool IsRegularOutput(string root) { return File.Exists(AgentData.Inside(root,RegularConfig)); }
        internal static string LockPath(string root) { return AgentData.Inside(root,IsRegularOutput(root)?@"影片制作\视频任务.lock":@"一键成片\自动制作.lock"); }
        internal static FileStream Lock(string root)
        {
            string lockFile = LockPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile));
            return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        private static string ProgressPath(string root) { return AgentData.Inside(root,IsRegularOutput(root)?@"影片制作\视频进度.json":StateFile); }
        internal static FlowConfig Config(string root)
        {
            try
            {
                return AgentData.Read<FlowConfig>(AgentData.Inside(root,ConfigFile)) ?? AgentData.Read<FlowConfig>(AgentData.Inside(root,RegularConfig));
            }
            catch (ArgumentException error) { throw new InvalidDataException("项目配置文件已损坏。", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("项目配置文件已损坏。", error); }
        }
        internal static FlowState State(string root)
        {
            FlowState state;
            try { state = AgentData.Read<FlowState>(ProgressPath(root)); }
            catch (ArgumentException error) { throw new InvalidDataException("项目制作进度记录已损坏。", error); }
            catch (InvalidOperationException error) { throw new InvalidDataException("项目制作进度记录已损坏。", error); }
            if (state == null)
            {
                if (IsRegularOutput(root)) return new FlowState { Status = "待确认费用", Phase = "视频生成" };
                throw new InvalidDataException("一键成片项目缺少制作进度。");
            }
            return state;
        }
        internal static void Save(string root, FlowState state)
        {
            state.Utc = AnalysisData.Now();
            AgentData.Save(ProgressPath(root), state);
        }
        internal static string Status(string root)
        {
            try { var s = State(root); return s.Status + (s.Phase == null ? "" : " · " + s.Phase); }
            catch (Exception e) { if (!(e is IOException || e is InvalidDataException || e is InvalidOperationException || e is ArgumentException || e is UnauthorizedAccessException)) throw; return "请检查项目制作记录"; }
        }

        internal static string Create(ProjectStore store, string parent, FlowDraft draft, string exe, string model,
            AssetChannel channel, Func<FlowConflict, FlowSkillPlan, int> choose, Func<string[], bool> continueWithGaps,
            Action<string> progress, CancellationToken token, string draftFile = null)
        {
            if (draft.Ratio != "9:16") throw new InvalidDataException("一键成片模式仅支持竖屏 9:16。");
            ProjectStore.ValidateName((draft.Name ?? "").Trim());
            if (Directory.Exists(Path.Combine(parent, draft.Name.Trim()))) throw new IOException("已有同名项目，请修改项目名称。");
            if (!File.Exists(draft.Script)) throw new FileNotFoundException("请选择可以读取的剧本文件。");
            if (FlowChannels.IsResponsesApi(draft.Channel))
            {
                CodexAuthMode.EnsureResponsesApi();
                channel = FlowChannels.ResponsesApiChannel();
            }
            if (draft.Channel != (channel == null ? "builtin" : channel.Id)) throw new InvalidOperationException("该出图渠道没有通过本机检测，请重新选择。");
            if (ImageApiProfiles.IsProfile(draft.Channel)) ImageApiProfiles.Key(channel);
            VideoPlatform.For(draft.Video,draft.Ratio,token);
            progress("正在读取所选 Skill 及参考文件");
            token.ThrowIfCancellationRequested();
            List<FlowPackage> packages;
            var plan = FlowSkills.CreateV2Plan(draft, out packages);
            var final = plan.routes.Single(r => r.stage == "final-prompt");
            progress("正在保存制作方案并导入剧本");
            string result = store.Create(parent, draft.Name, "一键成片项目", folder =>
            {
                FlowSkills.Snapshot(folder, packages);
                AgentData.Save(FlowSkills.PlanPath(folder), plan);
                ScriptDocument doc = null;
                store.ImportScript(folder, draft.Script, copied =>
                {
                    if (PdfScript.IsPdf(copied)) PdfScript.Prepare(copied, progress, token);
                    doc = ScriptDocument.Read(copied, null);
                });
                AgentData.Save(AgentData.Inside(folder, ConfigFile), new FlowConfig {
                    Video = draft.Video, Ratio = draft.Ratio, Manual = draft.Manual, Channel = draft.Channel, ChannelName = channel == null ? "Codex 内置生图" : channel.Name,
                    PipelineMode = "v2-builtin", FinalPromptSkillMode = draft.FinalPromptSkillMode == "custom" ? "custom" : "builtin",
                    FinalPromptSkillName = plan.Skills[final.members[0]-1].Name, Combination = plan.Key, ScriptHash = doc.Hash });
                Save(folder, new FlowState());
                token.ThrowIfCancellationRequested();
                if (draft.DefaultCustomPromptSkill && draft.FinalPromptSkillMode == "custom")
                    AgentData.Save(DefaultsFile, draft.FinalPromptSkillFile);
                else if (draft.FinalPromptSkillMode == "custom")
                {
                    string savedDefault = null;
                    try { savedDefault = AgentData.Read<string>(DefaultsFile); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    if (!String.IsNullOrEmpty(savedDefault) && String.Equals(savedDefault, draft.FinalPromptSkillFile, StringComparison.OrdinalIgnoreCase))
                        File.Delete(DefaultsFile);
                }
            }, draft.Language);
            return result;
        }
    }
}
