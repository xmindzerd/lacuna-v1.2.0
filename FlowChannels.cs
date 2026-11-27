using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneClickFilm
{
    internal static class FlowChannels
    {
        internal const string Dreamina = "dreamina-local";

        internal const string ResponsesApi = "responses-api";
        internal const string ResponsesApiName = "OpenAI Responses API 生图";

        internal static bool IsResponsesApi(string id) { return id == ResponsesApi; }
        internal static bool IsResponsesApi(AssetChannel channel) { return channel != null && IsResponsesApi(channel.Id); }
        internal static bool IsNative(string id) { return id == "builtin" || IsResponsesApi(id); }
        internal static AssetChannel ResponsesApiChannel()
        {
            return new AssetChannel { Id = ResponsesApi, Name = ResponsesApiName, Driver = ResponsesApi };
        }

        internal static AssetChannel InstalledDreamina()
        {
            var paths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\dreamina.exe"),
                CodexConnection.FindOnPath("dreamina.exe"),
                @"E:\Codex文件\工具\即梦CLI\dreamina.exe"
            };
            string file = paths.FirstOrDefault(p => !String.IsNullOrEmpty(p) && File.Exists(p));
            return file == null ? null : new AssetChannel { Id = Dreamina, Name = "即梦 Dreamina CLI", Driver = Dreamina,
                Executable = Path.GetFullPath(file), CheckArguments = "user_credit" };
        }

        internal static List<AssetChannel> Available(CancellationToken token)
        {
            var options = AgentData.Options();
            var all = AssetChannels.Load();
            var dreamina = InstalledDreamina();
            if (dreamina != null && !all.Any(c => c.Id == Dreamina)) all.Add(dreamina);
            var result = new List<AssetChannel>();
            foreach (var c in all)
            {
                token.ThrowIfCancellationRequested();
                try { AssetChannels.CheckCli(c.Options(options), Path.GetDirectoryName(c.Executable), token); result.Add(c); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    if (!(e is IOException || e is InvalidOperationException || e is ArgumentException || e is TimeoutException ||
                        e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is InvalidDataException)) throw;
                }
            }
            return result;
        }

        internal static AssetChannel Channel(string id)
        {
            if (id == "builtin") return null;
            if (IsResponsesApi(id)) return ResponsesApiChannel();
            var channel = id == Dreamina ? InstalledDreamina() : AssetChannels.Load().FirstOrDefault(c => c.Id == id);
            if (channel == null) throw new InvalidOperationException("项目选择的图片 CLI 在本机不可用。请恢复渠道配置后继续。");
            return channel;
        }

        internal static void CheckDreamina(string exe, CancellationToken token)
        {
            var result = CodexConnection.Run(exe, "user_credit", 15000, cancellation: token, useSystemProxy: true);
            if (result.Code != 0) throw new InvalidOperationException("即梦 CLI 登录或服务检查未通过。");
        }

        // Keep the provider submission id before polling. Recovery only queries this id.
        internal static void SubmitDreamina(MediaRequest request, string folder, MediaReceipt receipt, CancellationToken cancel)
        {
            bool reference = !String.IsNullOrWhiteSpace(request.ReferencePath);
            var args = new List<string> { reference ? "image2image" : "text2image", "--prompt", request.Prompt, "--resolution_type", "2k", "--generate_num", "1" };
            if (reference) { args.Add("--images"); args.Add(request.ReferencePath); }
            if (ImageOutputSettings.IsValid(request.TargetSize)) { args.Add("--ratio"); args.Add(ImageOutputSettings.Ratio(request.TargetSize)); }
            var sent = CodexConnection.Run(request.Options.ImageCli, AgentRunner.Args(args), 90000, folder, cancellation: cancel, useSystemProxy: true);
            string id = Field(sent.Output, "submit_id");
            if (String.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,160}$"))
                throw new IOException("即梦未返回可核对的任务号，请在原渠道核对本次提交。");
            receipt.ExternalId = id;
            AgentData.Save(Path.Combine(folder, AgentMedia.ReceiptFile), receipt);
            var wait = Stopwatch.StartNew();
            int deadline = request.TimeoutMs > 0 ? request.TimeoutMs : 360000;
            while (wait.ElapsedMilliseconds < deadline)
            {
                if (QueryDreamina(request.Options.ImageCli, folder, receipt, cancel)) return;
                if (cancel.WaitHandle.WaitOne(3000)) cancel.ThrowIfCancellationRequested();
            }
            throw new TimeoutException("即梦任务仍待回图，已保存任务号。继续时会查询原任务。");
        }

        internal static string Field(string json, string key)
        {
            var match = Regex.Match(json ?? "", @"[""']?" + Regex.Escape(key) + @"[""']?\s*[:=]\s*[""']?([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static bool QueryDreamina(string exe, string folder, MediaReceipt receipt, CancellationToken cancel = default(CancellationToken))
        {
            if (receipt == null || !Regex.IsMatch(receipt.ExternalId ?? "", @"^[A-Za-z0-9_-]{1,160}$")) return false;
            var dir = Directory.CreateDirectory(AgentData.Inside(folder, "即梦回图")).FullName;
            var reply = CodexConnection.Run(exe, AgentRunner.Args(new[] { "query_result", "--submit_id", receipt.ExternalId, "--download_dir", dir }),
                45000, folder, cancellation: cancel, useSystemProxy: true);
            string status = Field(reply.Output, "gen_status");
            if (String.Equals(status, "fail", StringComparison.OrdinalIgnoreCase) || String.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                receipt.State = "平台已失败"; AgentData.Save(Path.Combine(folder, AgentMedia.ReceiptFile), receipt);
                throw new InvalidOperationException("即梦已报告此图片任务失败。");
            }
            if (reply.Code != 0) return false;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(p =>
                new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
            if (files.Count == 0) return false;
            if (files.Count != 1) throw new InvalidDataException("即梦交回多张图片，请核对原任务。");
            string fullFolder = Path.GetFullPath(folder).TrimEnd('\\', '/');
            string relative = Path.GetFullPath(files[0]).Substring(fullFolder.Length + 1);
            AgentMedia.ValidateOutput(AgentData.Inside(folder, relative), "图片");
            receipt.Output = relative; receipt.State = "已取回";
            AgentData.Save(Path.Combine(folder, AgentMedia.ReceiptFile), receipt);
            return true;
        }
    }
}
