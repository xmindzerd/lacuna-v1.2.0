using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneClickFilm
{
    // The interactive app-server protocol exposes hosted image item status, failure and output
    // even when exec's JSON stream contains only the assistant's progress message.
    internal sealed class NativeImageRetry
    {
        internal const string ToolReceipt = "image-tool.json";
        private readonly string folder, prompt, model, effort;
        private readonly IList<string> images;
        private readonly ImageRunTrace trace;
        private readonly CancellationToken cancel;
        private string session, turn;
        private bool sent, finished;
        private readonly AgentResult usage = new AgentResult();
        private int pollPending, pollingDisabled;
        private readonly HashSet<string> completedImages = new HashSet<string>(StringComparer.Ordinal);
        private readonly object writeLock = new object();
        private volatile bool imageStarted;
        private string delivered, failure;
        private StreamWriter input;

        internal NativeImageRetry(string folder, string prompt, AgentOptions options, IList<string> images, ImageRunTrace trace, CancellationToken cancel)
        {
            this.folder = folder; this.prompt = prompt; this.model = CodexAuthMode.EffectiveModel(options.Model); this.effort = "low";
            this.images = images; this.trace = trace; this.cancel = cancel;
        }

        internal string Run(string exe, int timeout)
        {
            try { return RunImage(exe, timeout); }
            finally { AssetUsageLedger.ModelResponse(usage); }
        }

        private string RunImage(string exe, int timeout)
        {
            CodexAuthMode.EnsureBuiltinImage();
            if(FlowCreation.Development)DevMonitor.CodexSend(null,"内置出图 / 结果核对","app-server",model,effort,null,prompt,null,images==null?0:images.Count);
            var args = new[] { "app-server", "--listen", "stdio://", "--enable", "image_generation",
                "--disable", "shell_tool", "--disable", "apps", "--disable", "plugins", "--disable", "multi_agent",
                "-c", "mcp_servers={}", "-c", "web_search=\"disabled\"", "-c", "approval_policy=\"never\"",
                "-c", "sandbox_mode=\"read-only\"" }.Concat(CodexAuthMode.Arguments()).ToArray();
            bool script = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            string argLine = AgentRunner.Args(args);
            var start = new ProcessStartInfo { FileName = script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe,
                Arguments = script ? "/d /s /v:off /c \"\"%ONE_CLICK_FILM_CODEX%\" " + argLine + "\"" : argLine, WorkingDirectory = folder,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            CodexAuthMode.Configure(start,true);
            if (script) start.EnvironmentVariables["ONE_CLICK_FILM_CODEX"] = exe;
            using (var child = Process.Start(start))
            using (var group = TaskProcessGroup.Attach(child))
            {
                var timer = Stopwatch.StartNew();
                var errorStream = child.StandardError;
                var outputStream = child.StandardOutput;
                var stdin = new StreamWriter(child.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                input = stdin;
                var stderr = Task.Run(() => { string line; while ((line = errorStream.ReadLine()) != null) trace.Diagnostic(CodexAuthMode.Redact(line)); });
                var protocol = Task.Run(() =>
                {
                    Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "one_click_film", version = Program.Version },
                        capabilities = new { experimentalApi = true } } });
                    string line;
                    while (!finished && (line = outputStream.ReadLine()) != null) Handle(CodexAuthMode.Redact(line));
                });
                var inspection = new AgentMedia.StableImage();
                long nextInspection = 0, nextHistory = 30000;
                try
                {
                    while (!((IAsyncResult)protocol).AsyncWaitHandle.WaitOne(120))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (timer.ElapsedMilliseconds >= timeout + (imageStarted ? ImageOutputSettings.ReturnGraceMs : 0)) throw new TimeoutException("图像事件等待超时。");
                        string id = Volatile.Read(ref session);
                        if (id == null || timer.ElapsedMilliseconds < nextInspection) continue;
                        nextInspection = timer.ElapsedMilliseconds + 1000;
                        string turnId = Volatile.Read(ref turn);
                        if (imageStarted && turnId != null && timer.ElapsedMilliseconds >= nextHistory &&
                            Volatile.Read(ref pollingDisabled) == 0 && Interlocked.CompareExchange(ref pollPending, 1, 0) == 0)
                        {
                            nextHistory = timer.ElapsedMilliseconds + 60000;
                            Task.Run(() => ReadCurrentItems(id));
                        }
                        var candidates = AgentMedia.BuiltinCandidates(id);
                        if (candidates.Length > 1) throw new InvalidDataException("单张任务出现多张候选图，请核对原任务。");
                        string path = Volatile.Read(ref delivered) ?? candidates.SingleOrDefault();
                        if (Volatile.Read(ref failure) == null && inspection.Check(path)) return path;
                    }
                    protocol.GetAwaiter().GetResult();
                    if (failure != null) throw new InvalidOperationException(failure);
                    if (delivered != null) return delivered;
                    if (session != null)
                    {
                        string file = AgentMedia.FindBuiltinOutput(session, true);
                        if (file != null) return file;
                    }
                    throw new InvalidOperationException(finished ? "模型结束了本轮，未交回可验证的图片。" : "图像事件连接提前关闭。");
                }
                finally
                {
                    if ((group == null || !group.Stop()) && !child.HasExited) CodexConnection.StopProcessTree(child);
                    lock (writeLock)
                    {
                        input = null;
                        try { stdin.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
                    }
                    Task.WhenAll(protocol, stderr).ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
        }

        private static IDictionary<string, object> Obj(IDictionary<string, object> obj, string key)
        { return AgentData.Value(obj, key) as IDictionary<string, object>; }

        private void Send(object value)
        {
            var json = AgentData.Json();
            json.MaxJsonLength = Int32.MaxValue;
            lock (writeLock)
            {
                if (input == null) throw new ObjectDisposedException("stdin");
                input.WriteLine(json.Serialize(value));
            }
        }

        private static object ReferenceAttachment(string path)
        {
            AgentMedia.ValidateOutput(path, "图片");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
            return new { type = "image", url = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path)) };
        }

        private void ReadCurrentItems(string id)
        {
            try
            {
                Send(new { id = 5, method = "thread/read", @params = new { threadId = id, includeTurns = true } });
            }
            catch (IOException) { Interlocked.Exchange(ref pollingDisabled, 1); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref pollingDisabled, 1); }
        }

        private void Handle(string line)
        {
            if (String.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith("{", StringComparison.Ordinal)) return;
            var json = AgentData.Json(); json.MaxJsonLength = 64 * 1024 * 1024;
            Dictionary<string, object> record;
            try { record = json.Deserialize<Dictionary<string, object>>(line); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            if (record == null) return;
            string method = AgentData.Str(record, "method");
            var p = Obj(record, "params");
            if (method.Length == 0) { Reply(record); return; }
            if (record.ContainsKey("id"))
            {
                // A headless image request never grants tool, account or filesystem permissions.
                Send(new { id = record["id"], error = new { code = -32601, message = "Unsupported request for an approved image task." } });
                return;
            }
            if (session == null || AgentData.Str(p, "threadId") != session) return;
            string notificationTurn = AgentData.Str(p, "turnId");
            if (turn != null && notificationTurn.Length != 0 && turn != notificationTurn) return;
            if (method == "item/started" || method == "item/completed") Item(Obj(p, "item"), method == "item/completed");
            if (method == "error")
            {
                var error = Obj(p, "error");
                if (Object.Equals(AgentData.Value(p, "willRetry"), true)) trace.Mark("图像服务响应中断，正在恢复连接");
                else Fail(AgentData.Str(error, "message"));
            }
            if (method == "thread/tokenUsage/updated")
            {
                var last = Obj(Obj(p, "tokenUsage"), "last");
                if (last != null && last.ContainsKey("inputTokens") && last.ContainsKey("outputTokens"))
                {
                    usage.HasUsage = true;
                    usage.Input = AgentData.Number(last, "inputTokens");
                    usage.Cached = AgentData.Number(last, "cachedInputTokens");
                    usage.Output = AgentData.Number(last, "outputTokens");
                }
            }
            if (method == "turn/completed")
            {
                var result = Obj(p, "turn");
                if (AgentData.Str(result, "status") == "failed") Fail(AgentData.Str(Obj(result, "error"), "message"));
                finished = true;
            }
        }

        private void Reply(IDictionary<string, object> record)
        {
            var error = Obj(record, "error");
            if (AgentData.Number(record, "id") == 5)
            {
                Interlocked.Exchange(ref pollPending, 0);
                if (error != null) { Interlocked.Exchange(ref pollingDisabled, 1); return; }
                var history = Obj(Obj(record, "result"), "thread");
                if (AgentData.Str(history, "id") != session) return;
                var turns = AgentData.Value(history, "turns") as System.Collections.IEnumerable;
                if (turns == null) return;
                foreach (var snapshot in turns.OfType<IDictionary<string, object>>())
                {
                    if (AgentData.Str(snapshot, "id") != turn) continue;
                    var entries = AgentData.Value(snapshot, "items") as System.Collections.IEnumerable;
                    if (entries != null) foreach (var item in entries.OfType<IDictionary<string, object>>())
                    {
                        if (AgentData.Str(item, "type") != "imageGeneration") continue;
                        string status = AgentData.Str(item, "status");
                        if (status == "completed" || status == "failed") Item(item, true);
                    }
                    if (AgentData.Str(snapshot, "status") == "failed")
                        Fail(AgentData.Str(Obj(snapshot, "error"), "message"));
                    break;
                }
                return;
            }
            if (error != null) { Fail(AgentData.Str(error, "message")); return; }
            var result = Obj(record, "result");
            switch (AgentData.Number(record, "id"))
            {
                case 1:
                    Send(new { method = "initialized", @params = new { } });
                    Send(new { id = 2, method = "modelProvider/capabilities/read", @params = new { } });
                    break;
                case 2:
                    if (!Object.Equals(AgentData.Value(result, "imageGeneration"), true)) { Fail("本机渠道未报告内置图片生成能力。"); return; }
                    Send(new { id = 3, method = "thread/start", @params = new { cwd = folder, ephemeral = false,
                        approvalPolicy = "never", sandbox = "read-only", model = String.IsNullOrWhiteSpace(model) ? null : model,
                        modelProvider = CodexAuthMode.Provider, developerInstructions = "按附图顺序使用参考图，仅用内置图像工具生成一张图。" } });
                    break;
                case 3:
                    var id = AgentData.Str(Obj(result, "thread"), "id");
                    Guid parsed;
                    if (sent || !Guid.TryParse(id, out parsed)) { Fail("未收到可绑定的唯一图像会话。"); return; }
                    sent = true;
                    File.WriteAllText(Path.Combine(folder, AgentMedia.BuiltinSessionFile), id);
                    Interlocked.Exchange(ref session, id);
                    var inputs = new List<object> { new { type = "text", text = prompt } };
                    inputs.AddRange(images.Select(ReferenceAttachment));
                    var parameters = new Dictionary<string, object> { { "threadId", id }, { "input", inputs } };
                    if (!String.IsNullOrWhiteSpace(effort)) parameters["effort"] = effort;
                    trace.Mark("提交已校验的原图附件与已确认提示词");
                    AssetUsageLedger.Dispatch("Codex 账号", true);
                    Send(new { id = 4, method = "turn/start", @params = parameters });
                    break;
                case 4:
                    Volatile.Write(ref turn, AgentData.Str(Obj(result, "turn"), "id"));
                    trace.Mark("平台已接收本轮请求"); break;
            }
        }

        private void Item(IDictionary<string, object> item, bool complete)
        {
            string type = AgentData.Str(item, "type");
            if (type == "agentMessage" && complete)
                trace.Event(new AgentEvent { Type = imageStarted ? "image_progress_message" : "message", Text = AgentData.Str(item, "text") });
            if (type != "imageGeneration") return;
            string itemId = AgentData.Str(item, "id");
            if (complete && !completedImages.Add(itemId) && delivered != null) return;
            var why = Obj(item, "failure");
            AgentData.Save(Path.Combine(folder, ToolReceipt), new {
                threadId = session, turnId = turn, itemId = AgentData.Str(item, "id"),
                generationId = AgentData.Str(item, "generationId"), fileId = AgentData.Str(item, "fileId"),
                status = AgentData.Str(item, "status"), terminalEvent = complete,
                failure = AgentData.Str(why, "type"), resetsAt = AgentData.Value(why, "resetsAt")
            });
            if (!complete) { imageStarted = true; trace.Mark("图像工具已开始生成"); return; }
            if (why != null || AgentData.Str(item, "status") == "failed")
            {
                AgentData.Save(Path.Combine(folder, AgentMedia.ReceiptFile), new MediaReceipt {
                    State = "平台已失败", ExternalId = AgentData.Str(item, "generationId")
                });
                Fail(AgentData.Str(why, "type") == "usageLimitExceeded" ? "图像生成已达到平台用量限额。" : "图像工具返回失败。");
                return;
            }
            if (completedImages.Count > 1) { delivered = null; Fail("单张请求出现多个图像结果，请核对原任务。"); return; }
            trace.Mark("图像工具已返回结果，正在校验文件");
            var files = AgentMedia.BuiltinCandidates(session);
            if (files.Length > 1) { Fail("单张请求出现多张候选图，请核对原任务。"); return; }
            if (files.Length == 1) { Volatile.Write(ref delivered, files[0]); return; }
            var data = AgentData.Str(item, "result");
            if (String.IsNullOrEmpty(data)) return;
            if (data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = data.IndexOf(',');
                if (comma < 0 || !data.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图像工具返回了未知的图片编码。");
                data = data.Substring(comma + 1);
            }
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length < 12) { Fail("图像工具返回的数据不完整。"); return; }
            string ext = bytes[0] == 0xff ? ".jpg" : bytes[0] == 0x52 ? ".webp" : ".png";
            string path = Path.Combine(folder, "native-image" + ext);
            File.WriteAllBytes(path, bytes);
            AssetProduction.Inspect(path, new AssetAttempt());
            Volatile.Write(ref delivered, path);
        }

        private void Fail(string detail)
        {
            failure = String.IsNullOrWhiteSpace(detail) ? "图像通路返回失败。" : AgentData.Short(detail, 350);
            trace.Mark("图像通路返回失败");
            finished = true;
        }
    }
}
