using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneClickFilm
{
    public sealed class AssetUsageBatch
    {
        public string Id, Operation, StartedUtc, FinishedUtc, ChannelSummary;
        public Dictionary<string, int> ChannelCounts = new Dictionary<string, int>();
        public List<string> RetryReasons = new List<string>();
        public int PromptCalls, VerifyCalls, BuiltinImageCalls, ExternalCliCalls, VisualCheckCalls;
        public int SucceededCount, RetryCount, UsageKnownCalls, UsageMissingCalls;
        public long InputTokens, CachedInputTokens, OutputTokens;
        internal int ModelCalls { get { return PromptCalls + VerifyCalls + BuiltinImageCalls + VisualCheckCalls; } }

        internal string Describe()
        {
            string tokens = ModelCalls == 0 ? "未调用模型（仅外部 CLI）" : UsageKnownCalls == 0 ? "未取得" :
                InputTokens + " / " + CachedInputTokens + " / " + OutputTokens + (UsageMissingCalls > 0 ? "（部分未取得）" : "");
            DateTime start;
            string time = DateTime.TryParse(StartedUtc, out start) ? start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : StartedUtc;
            return time + "  " + Operation + " · " + (String.IsNullOrEmpty(FinishedUtc) ? "未结束" : "已结束") +
                "\n" + ChannelSummary + "\n写提示词 " + PromptCalls + " · 核实/审核 " + VerifyCalls +
                " · 内置出图 " + BuiltinImageCalls + " · 外部 CLI 直连 " + ExternalCliCalls + " · 视觉自检 " + VisualCheckCalls +
                "\n成功 " + SucceededCount + " · 重试 " + RetryCount + (RetryReasons.Count == 0 ? "" : " · " + String.Join("、", RetryReasons)) +
                "\nToken（输入 / 缓存输入 / 输出） " + tokens;
        }
    }

    // ExecutionContext carries the batch into Task.Run and Parallel.ForEach even in team builds.
    internal sealed class AssetUsageLedger : IDisposable
    {
        internal const string Prompt = "准备提示词", Verify = "批量核实提示词", AutoReview = "一键 Codex 分析审核", Visual = "视觉自检", Image = "资产出图";
        private const string External = "外部平台";
        private static readonly AsyncLocal<AssetUsageLedger> current = new AsyncLocal<AssetUsageLedger>();
        private readonly AssetUsageLedger previous;
        private readonly string path;
        private readonly AssetUsageBatch batch;

        private AssetUsageLedger(string root, string analysis, string operation)
        {
            path = FileName(root, analysis);
            batch = new AssetUsageBatch { Id = AgentData.Id(), StartedUtc = AnalysisData.Now(), Operation = operation };
            previous = current.Value;
            current.Value = this;
        }

        internal static IDisposable Begin(string root, string analysis, string operation)
        {
            return new AssetUsageLedger(root, analysis, operation);
        }

        internal static string ModelChannel { get { return CodexAuthMode.IsApi ? "Responses API" : "Codex 账号"; } }

        private static string FileName(string root, string analysis)
        {
            return AgentData.Inside(AssetProduction.Folder(root, analysis), "用量记录.json");
        }

        internal static List<AssetUsageBatch> Recent(string root, string analysis)
        {
            return Read(FileName(root, analysis));
        }

        private static List<AssetUsageBatch> Read(string file)
        {
            var batches = AgentData.Read<List<AssetUsageBatch>>(file) ?? new List<AssetUsageBatch>();
            foreach (var batch in batches)
            {
                // Earlier development builds counted each external CLI dispatch as missing model usage.
                if (batch.ExternalCliCalls > 0 && batch.UsageMissingCalls == batch.ModelCalls - batch.UsageKnownCalls + batch.ExternalCliCalls)
                    batch.UsageMissingCalls -= batch.ExternalCliCalls;
            }
            return batches;
        }

        private void Persist()
        {
            using (var mutex = new Mutex(false, @"Local\LacunaAssetUsage-" + AgentData.Hash(path.ToUpperInvariant())))
            {
                try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    var records = Read(path);
                    records.RemoveAll(entry => entry.Id == batch.Id);
                    batch.ChannelSummary = String.Join(" · ", batch.ChannelCounts.Select(p => p.Key + " " + p.Value));
                    records.Add(batch);
                    AgentData.Save(path, records.OrderByDescending(b => b.StartedUtc, StringComparer.Ordinal).Take(50).ToList());
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void Update(Action<AssetUsageBatch> change)
        {
            var scope = current.Value;
            if (scope == null) return;
            lock (scope.batch)
            {
                change(scope.batch);
                scope.Persist();
            }
        }

        internal static void Dispatch(string channel, bool image = false)
        {
            Update(b => {
                int n;
                b.ChannelCounts.TryGetValue(channel, out n);
                b.ChannelCounts[channel] = n + 1;
                if (channel == External) b.ExternalCliCalls++;
                else
                {
                    b.UsageMissingCalls++;
                    if (image) b.BuiltinImageCalls++;
                    else if (b.Operation == Visual) b.VisualCheckCalls++;
                    else if (b.Operation.Contains("审核") || b.Operation.Contains("核实")) b.VerifyCalls++;
                    else b.PromptCalls++;
                }
            });
        }

        // A dispatch stays unknown until its provider returns actual usage (including a genuine zero).
        internal static void ModelResponse(AgentResult result)
        {
            if (result == null || !result.HasUsage) return;
            Update(b => {
                b.UsageMissingCalls--; b.UsageKnownCalls++;
                b.InputTokens += result.Input; b.CachedInputTokens += result.Cached; b.OutputTokens += result.Output;
            });
        }

        internal static void Success(int count) { Update(b => b.SucceededCount += count); }

        internal static void Retry(string reason)
        {
            Update(b => {
                b.RetryCount++;
                if (!b.RetryReasons.Contains(reason)) b.RetryReasons.Add(reason);
            });
        }

        public void Dispose()
        {
            current.Value = previous;
            lock (batch)
            {
                if (batch.ChannelCounts.Count == 0 && batch.RetryCount == 0 && batch.SucceededCount == 0) return;
                batch.FinishedUtc = AnalysisData.Now();
                Persist();
            }
        }
    }
}
