using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneClickFilm
{
    public sealed class AssetChannel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Executable { get; set; }
        public string Driver { get; set; }
        public string BaseUrl { get; set; }
        public string ImageModel { get; set; }
        public string ImageKeyRevision { get; set; }
        public string MainModel { get; set; }
        public string ImageEffort { get; set; }
        public string Arguments { get; set; }
        public string OutputSizes { get; set; }
        public string CheckArguments { get; set; }
        public string CheckedUtc { get; set; }
        public string Status { get; set; }

        internal AgentOptions Options(AgentOptions current)
        {
            var options = AgentData.Json().Deserialize<AgentOptions>(AgentData.Json().Serialize(current));
            options.ImageCli = Executable;
            options.ImageDriver = Driver;
            options.ImageArgs = Arguments;
            options.ImageCheckArgs = CheckArguments;
            return options;
        }
    }

    internal static class AssetChannels
    {
        private static string FilePath { get { return Path.Combine(AgentData.Local, "图片平台.json"); } }

        internal static List<AssetChannel> Load()
        {
            var result = AgentData.Read<List<AssetChannel>>(FilePath) ?? new List<AssetChannel>();
            var legacy = AgentData.Options();
            result.RemoveAll(p => p.Id == "legacy" &&
                (p.Executable != legacy.ImageCli || p.Arguments != legacy.ImageArgs));
            if (!String.IsNullOrWhiteSpace(legacy.ImageCli) && !result.Any(p =>
                String.Equals(p.Executable, legacy.ImageCli, StringComparison.OrdinalIgnoreCase) && p.Arguments == legacy.ImageArgs))
            {
                result.Add(new AssetChannel { Id = "legacy", Name = Path.GetFileNameWithoutExtension(legacy.ImageCli),
                    Executable = legacy.ImageCli, Arguments = legacy.ImageArgs, Status = "待配置健康检查" });
            }
            return result;
        }

        internal static void Save(List<AssetChannel> channels)
        {
            AgentData.Save(FilePath, channels);
        }

        internal static FileStream LockForWrite()
        {
            Directory.CreateDirectory(AgentData.Local);
            return new FileStream(Path.Combine(AgentData.Local, "图片平台.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }

        internal static List<AssetChannel> Detect(string codex, AgentOptions options, CancellationToken cancel)
        {
            using (LockForWrite())
            {
                var channels = Load();
                foreach (AssetChannel channel in channels)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (ImageApiProfiles.IsProfile(channel.Id)) continue;
                    try
                    {
                        CheckCli(channel.Options(options), Path.GetDirectoryName(channel.Executable), cancel);
                        channel.Status = "可用";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { channel.Status = AgentData.Short(error.Message, 150); }
                    channel.CheckedUtc = AnalysisData.Now();
                }
                Save(channels);
                return channels;
            }
        }

        internal static void Store(AssetChannel channel)
        {
            using (LockForWrite())
            {
                var items = Load();
                items.RemoveAll(p => p.Id == channel.Id);
                if (channel.Id == "legacy") channel.Id = AgentData.Id();
                items.Add(channel);
                Save(items);
            }
        }

        internal static void CheckCli(AgentOptions options, string folder, CancellationToken cancel)
        {
            AgentMedia.Validate(options, "图片", "cli");
            if (options.ImageDriver == FlowChannels.Dreamina) { FlowChannels.CheckDreamina(options.ImageCli, cancel); return; }
            var lines = (options.ImageCheckArgs ?? "").Split(new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (lines.Count == 0 || lines.Any(x => x.Contains("{prompt") || x.Contains("{output}")))
                throw new InvalidOperationException("请配置此平台的只读健康检查参数，例如平台提供的登录状态或服务检查。");
            if (lines.All(x => x == "--version" || x == "-v" || x == "-V" || x == "--help" || x == "-h"))
                throw new InvalidOperationException("版本或帮助只能检查程序存在，需配置能检查平台登录或服务状态的命令。");
            cancel.ThrowIfCancellationRequested();
            var probe = CodexConnection.Run(options.ImageCli, AgentRunner.Args(lines), 15000, folder, cancellation: cancel);
            if (probe.Code != 0)
                throw new InvalidOperationException("外部 CLI 健康检查失败（退出码 " + probe.Code + "），已停止提交。");
        }

        internal static string CheckResponsesApi()
        {
            CodexAuthMode.EnsureResponsesApi();
            return "OpenAI Responses API 独立生图已配置 · 模型 " + CodexAuthMode.Model + " · 实际出图以本次返回为准";
        }

        internal static string CheckBuiltin(string codex, CancellationToken cancel)
        {
            CodexAuthMode.EnsureBuiltinImage();
            if (CodexAuthMode.IsApi) return "OpenAI Responses API 内置出图已配置 · 实际出图以本次返回为准";
            var account = CodexConnection.Check(codex);
            if (account.State != CodexState.SignedIn)
                throw new InvalidOperationException("Codex 内置出图不可用，请检查本机 Codex 登录。");
            var result = CodexConnection.Run(codex, "features list", 15000, cancellation: cancel);
            if (result.Code != 0 || !Regex.IsMatch(result.Output, @"(?m)^image_generation[ \t]+\S+(?:[ \t]+\S+)*[ \t]+true[ \t]*\r?$"))
                throw new InvalidOperationException("当前 Codex 未报告启用内置图片生成。请在 Codex 中检查后重试。");
            return "已登录，图片生成已启用 · 实际出图以本次返回为准";
        }
    }
}