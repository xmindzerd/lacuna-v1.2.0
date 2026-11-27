using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace OneClickFilm
{
    public sealed class AgentOptions
    {
        public string Model { get; set; }
        public string ReasoningEffort { get; set; }
        public string ImageRoute { get; set; }
        public string VideoRoute { get; set; }
        public string ImageCli { get; set; }
        public string ImageDriver { get; set; }
        public string ImageArgs { get; set; }
        public string ImageCheckArgs { get; set; }
        public string VideoCli { get; set; }
        public string VideoArgs { get; set; }
        public string SubmitUrl { get; set; }
        public string StatusUrl { get; set; }
        public string TokenVariable { get; set; }
        public string ApiBody { get; set; }
        public string IdField { get; set; }
        public string ResultField { get; set; }
        public string StateField { get; set; }
        public string SuccessState { get; set; }
        public string FailureState { get; set; }
        public string ImageCost { get; set; }
        public string VideoCost { get; set; }
        public string PerTaskLimit { get; set; }
        public string DailyLimit { get; set; }
    }

    public sealed class AgentMessage
    {
        public string Who { get; set; }
        public string Text { get; set; }
        public SkillReference Skill { get; set; }
    }

    public sealed class AgentChat
    {
        public string Id { get; set; }
        public string Session { get; set; }
        public string HttpsSession { get; set; }
        public string ApiSession { get; set; }
        public string ApiEndpoint { get; set; }
        public string Title { get; set; }
        public string State { get; set; }
        public string Model { get; set; }
        public List<AgentMessage> Messages { get; set; }
        public long Input { get; set; }
        public long Output { get; set; }
        public long Cached { get; set; }
        public long Context { get; set; }
        public bool UsageMissing { get; set; }
        public bool UsageReceived { get; set; }
        public int Window { get; set; }
    }

    public sealed class AgentJob
    {
        public string Id { get; set; }
        public string Prompt { get; set; }
        public string OriginalPrompt { get; set; }
        public string State { get; set; }
        public string Path { get; set; }
        public string Detail { get; set; }
        public string ExternalId { get; set; }
        public string StartedUtc { get; set; }
        public string LastCheck { get; set; }
        public string ReconciledUtc { get; set; }

        internal bool NeedsReconciliation
        {
            get { return State == "运行中" || (State ?? "").StartsWith("需核对", StringComparison.Ordinal); }
        }
    }

    public sealed class AgentBatch
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Route { get; set; }
        public string Summary { get; set; }
        public string Profile { get; set; }
        public List<AgentJob> Jobs { get; set; }
        public List<string> ReferencePaths { get; set; }
        public SkillReference Skill { get; set; }
        public SkillReference CompareSkill { get; set; }

        internal bool Blocked { get { return Jobs.Any(j => j.NeedsReconciliation); } }
    }

    public sealed class AgentHistory
    {
        public List<AgentChat> Chats { get; set; }
        public List<AgentBatch> Batches { get; set; }
    }

    internal static class AgentData
    {
        internal static string Local
        {
            get
            {
                return SystemData.Active.Folder;
            }
        }

        internal static string Id() { return Guid.NewGuid().ToString("N"); }
        internal static bool ValidId(string value)
        {
            return value != null && Regex.IsMatch(value, @"^[a-f0-9]{32}$", RegexOptions.IgnoreCase);
        }

        internal static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 96 };
        }

        internal static string DirectoryFor(string project)
        {
            return Inside(project ?? Local, "Codex助手");
        }

        internal static string Inside(string root, string relative)
        {
            string folder = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(Path.Combine(folder, relative ?? ""));
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                !file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || file == folder)
                throw new InvalidOperationException("文件引用超出当前项目范围。");
            string cursor = folder.TrimEnd('\\', '/');
            foreach (string part in file.Substring(folder.Length).Split(Path.DirectorySeparatorChar))
            {
                cursor = Path.Combine(cursor, part);
                if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                    (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("项目引用包含链接目录或链接文件。");
            }
            return file;
        }

        internal static T Read<T>(string file) where T : class
        {
            string content;
            try
            {
                content = WithFileSharingRetry(delegate
                {
                    using (var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
                });
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            return Json().Deserialize<T>(content);
        }

        internal static T ParseOutput<T>(string text) where T : class
        {
            try
            {
                if(!String.IsNullOrWhiteSpace(text))
                {
                    var result=Json().Deserialize<T>(text);
                    if(result!=null)return result;
                }
            }
            catch(Exception e)
            {
                if(!(e is ArgumentException || e is InvalidOperationException || e is FormatException ||
                    e is OverflowException || e is InvalidCastException))throw;
                throw new InvalidDataException("Codex 本轮已结束，但结构化正文格式无效。",e);
            }
            throw new InvalidDataException("Codex 本轮已结束，但缺少结构化正文。");
        }

        // Windows briefly takes an exclusive handle during ReplaceFile, even for atomic JSON updates.
        private static T WithFileSharingRetry<T>(Func<T> operation)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return operation(); }
                catch (IOException e)
                {
                    int error = e.HResult & 0xffff;
                    if ((error != 32 && error != 33 && error != 183) || attempt == 8) throw;
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }

        internal static void Save(string file, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = Path.Combine(Path.GetDirectoryName(file), Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temp, Json().Serialize(value), new UTF8Encoding(false));
                WithFileSharingRetry(delegate
                {
                    if (File.Exists(file)) File.Replace(temp, file, null);
                    else File.Move(temp, file);
                    return true;
                });
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static AgentOptions Options()
        {
            AgentOptions o = null;
            try { o = Read<AgentOptions>(Path.Combine(Local, "Codex渠道-V0.1.21.json")); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (o == null) o = new AgentOptions();
            if (String.IsNullOrEmpty(o.ImageRoute)) o.ImageRoute = "builtin";
            if (String.IsNullOrEmpty(o.VideoRoute)) o.VideoRoute = "cli";
            return o;
        }

        internal static void SaveOptions(AgentOptions options)
        {
            Save(Path.Combine(Local, "Codex渠道-V0.1.21.json"), options);
        }

        internal static AgentHistory LoadHistory(string project)
        {
            AgentHistory h = null;
            try { h = Read<AgentHistory>(Path.Combine(DirectoryFor(project), "记录.json")); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (h == null) h = new AgentHistory();
            if (h.Chats == null) h.Chats = new List<AgentChat>();
            else h.Chats.RemoveAll(c => c == null);
            if (h.Batches == null) h.Batches = new List<AgentBatch>();
            else h.Batches.RemoveAll(b => b == null);
            foreach (AgentChat chat in h.Chats)
            {
                if (chat.Messages == null) chat.Messages = new List<AgentMessage>();
                else chat.Messages.RemoveAll(m => m == null);
                if (chat.State == "运行中") chat.State = "上次中断 · 可重新发送";
            }
            foreach (AgentBatch batch in h.Batches)
            {
                if (batch.Jobs == null) batch.Jobs = new List<AgentJob>();
                else batch.Jobs.RemoveAll(j => j == null);
            }
            foreach (AgentJob job in h.Batches.SelectMany(b => b.Jobs))
            {
                if (job.State == "运行中") job.State = "需核对 · 未自动重提";
                if (job.Path == null && ValidId(job.Id) && String.IsNullOrEmpty(job.ExternalId))
                {
                    var receipt = AgentMedia.ReadJobReceipt(AgentMedia.JobFolder(project, job), job.LastCheck)
                        ?? AgentMedia.ReadJobReceipt(AgentMedia.JobFolder(project, job), null);
                    if (receipt != null) job.ExternalId = receipt.ExternalId;
                }
            }
            return h;
        }

        internal static void SaveHistory(string project, AgentHistory h)
        {
            Save(HistoryFile(project), h);
        }

        internal static string HistoryFile(string project)
        {
            return Inside(DirectoryFor(project), "记录.json");
        }

        internal static object Value(IDictionary<string, object> o, string key)
        {
            object value;
            return o != null && o.TryGetValue(key, out value) ? value : null;
        }

        internal static string Str(IDictionary<string, object> o, string key)
        {
            return Convert.ToString(Value(o, key), CultureInfo.InvariantCulture) ?? "";
        }

        internal static long Number(IDictionary<string, object> o, string key)
        {
            long n;
            return Int64.TryParse(Str(o, key), out n) ? Math.Max(0, n) : 0;
        }

        internal static string Short(string text, int max)
        {
            if (String.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max);
        }

        internal static string Hash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
    }

    internal sealed class AgentModel
    {
        internal string Key, Name, DefaultEffort;
        internal string[] Efforts = new string[0];
        internal int Window;
        public override string ToString() { return Name; }
    }

    internal sealed class AgentEvent
    {
        internal string Type, Text, RequestId, ReportedModel;
        internal bool HasUsage;
        internal long Input, Output, Cached, Reasoning;
    }

    internal sealed class AgentResult
    {
        internal string Session, Answer, TransportNote, StructuredText;
        internal long Input, Output, Cached, Reasoning;
        internal int Calls;
        internal bool HasUsage;
    }

    internal static class AgentRunner
    {
        internal static string CodexHome
        {
            get
            {
                return Environment.GetEnvironmentVariable("CODEX_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
        }

        internal static string ConfiguredModel()
        {
            try
            {
                string config = Path.Combine(CodexHome, "config.toml");
                return File.Exists(config) ? Regex.Match(File.ReadAllText(config),
                    @"(?m)^\s*model\s*=\s*[""']([\w.-]+)[""']").Groups[1].Value : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        internal static List<AgentModel> Models()
        {
            string current = ConfiguredModel();
            var models = new List<AgentModel> { new AgentModel { Key = "", Name = "Codex 默认模型" } };
            try
            {
                var cache = AgentData.Read<Dictionary<string, object>>(Path.Combine(CodexHome, "models_cache.json"));
                var list = AgentData.Value(cache, "models") as System.Collections.IEnumerable;
                if (list != null)
                    foreach (object item in list)
                    {
                        var data = item as IDictionary<string, object>;
                        string key = AgentData.Str(data, "slug");
                        if (!ValidModel(key) || AgentData.Str(data, "visibility") == "hide" || models.Any(x => x.Key == key))
                            continue;
                        long limit = AgentData.Number(data, "context_window");
                        long percentage = AgentData.Number(data, "effective_context_window_percent");
                        string label = AgentData.Str(data, "display_name");
                        var levels = AgentData.Value(data, "supported_reasoning_levels") as System.Collections.IEnumerable;
                        string[] efforts = levels == null ? new string[0] : levels.Cast<object>()
                            .Select(x => AgentData.Str(x as IDictionary<string, object>, "effort")).Where(ValidEffort).Distinct().ToArray();
                        models.Add(new AgentModel { Key = key, Name = label.Length == 0 ? key : label, Efforts = efforts,
                            Window = limit < 100000000 ? (int)(limit * (percentage > 0 && percentage <= 100 ? percentage : 100) / 100) : 0 });
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            var live=CodexAppServer.LiveModels;
            if(live.Length>0)
            {
                var cached=models.ToDictionary(x=>x.Key);
                models.RemoveRange(1,models.Count-1);
                foreach(var m in live)
                    models.Add(new AgentModel{Key=m.Key,Name=m.Name,Efforts=m.Efforts,DefaultEffort=m.DefaultEffort,
                        Window=cached.ContainsKey(m.Key)?cached[m.Key].Window:0});
            }
            if (ValidModel(current))
            {
                models[0].Name += " · " + current;
                models[0].Window = models.Where(x => x.Key == current).Select(x => x.Window).FirstOrDefault();
                var configured = models.FirstOrDefault(x => x.Key == current);
                if (configured != null) models[0].Efforts = configured.Efforts;
            }
            return models;
        }

        internal static bool ValidModel(string key)
        {
            return !String.IsNullOrEmpty(key) && Regex.IsMatch(key, @"\A[a-zA-Z0-9][a-zA-Z0-9_.:/-]{0,119}\z") &&
                !key.Contains("..") && !key.Contains("//") && !key.EndsWith("/", StringComparison.Ordinal);
        }

        internal static bool ValidEffort(string value)
        {
            return new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" }.Contains(value);
        }

        internal static string Quote(string text)
        {
            return "\"" + Regex.Replace(Regex.Replace(text ?? "", @"(\\*)""", "$1$1\\\""), @"(\\+)$", "$1$1") + "\"";
        }

        internal static string Args(IEnumerable<string> args)
        {
            return String.Join(" ", args.Select(Quote));
        }

        internal static string BridgeCommand(string project)
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            return "codex mcp add oneclick_film -- '" + exe.Replace("'", "''") +
                "' --agent-mcp '" + project.Replace("'", "''") + "'";
        }

        private static string ReadStructuredFile(string path)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    return !String.IsNullOrWhiteSpace(text) && AgentData.Json().DeserializeObject(text) is IDictionary<string, object> ? text : null;
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static bool SharingConflict(IOException e)
        {
            int code = e.HResult & 0xffff;
            return code == 32 || code == 33;
        }

        internal static AgentResult Run(string exe, string work, string text, string model, string session,
            string schema, string output, IList<string> images, string project, string jobFolder,
            bool builtinImage, Action<AgentEvent> notify, CancellationToken cancel, bool forkSession = false,
            SelectedSkill skill = null, int timeoutMs = 0, bool skillAudit = false, Func<bool> resultReady = null, Action<string> onDiagnostic = null, int idleTimeoutMs = 0, bool assetImage = false, string effortOverride = null)
        {
            model = CodexAuthMode.EffectiveModel(model);
            Stopwatch monitoring=null;
            string monitorProject=project??DevMonitor.Context;
            if(FlowCreation.Development){
                monitoring=Stopwatch.StartNew();
                var downstream=notify;
                long last=-5000;
                notify=ev=>{
                    if(ev.Type=="delta" || ev.Type=="reasoning"){
                        long now=monitoring.ElapsedMilliseconds;
                        if(now-Interlocked.Read(ref last)>=5000){Interlocked.Exchange(ref last,now);DevMonitor.Log(monitorProject,"CODEX-EV",ev.Type+" · 正在生成");}
                    }else if(ev.Type=="session" || ev.Type=="step" || ev.Type=="retry" || ev.Type=="transport" || ev.Type=="tool_error" || ev.Type=="failed")
                        DevMonitor.Log(monitorProject,"CODEX-EV",ev.Type+" · "+ev.Text);
                    if(downstream!=null)downstream(ev);
                };
                var diagnostic=onDiagnostic;
                onDiagnostic=line=>{if(!String.IsNullOrWhiteSpace(line))DevMonitor.Log(monitorProject,"CODEX-EV",line);if(diagnostic!=null)diagnostic(line);};
            }
            try
            {
                var result = Execute(exe, work, text, model, session, schema, output, images, project, jobFolder,
                    builtinImage, notify, cancel, forkSession, skill, timeoutMs, skillAudit, resultReady, onDiagnostic,
                    idleTimeoutMs, assetImage, effortOverride, monitorProject);
                if(monitoring!=null)DevMonitor.Log(monitorProject,"CODEX-RX","完成 · "+monitoring.ElapsedMilliseconds+" ms · "+(result.HasUsage ? "输入 "+result.Input+" / 缓存 "+result.Cached+" / 输出 "+result.Output+(result.Reasoning > 0 ? " / 推理 "+result.Reasoning : "") : "用量未取得"));
                return result;
            }
            catch (Exception error)
            {
                if(monitoring!=null)DevMonitor.Log(monitorProject,"CODEX-RX","失败 · "+monitoring.ElapsedMilliseconds+" ms · "+error.Message);
                throw;
            }
        }

        // Keep the original in-progress receipt when a disk error occurs after submission.
        // Retrying a completed provider turn just to write its receipt can incur another charge.
        internal sealed class ApiRunLog
        {
            private readonly string file;
            private readonly List<object> requests = new List<object>();
            private readonly Dictionary<string,object> fields = new Dictionary<string,object>();
            internal ApiRunLog(string model)
            {
                string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + AgentData.Id();
                file = AgentData.Inside(AgentData.Local, Path.Combine("Codex运行记录", id + ".json"));
                fields["通道"] = "Responses API";
                fields["请求记录"] = requests;
                fields["开始 UTC"] = DateTime.UtcNow.ToString("o");
                fields["状态"] = "已启动";
                AddId("请求模型", model);
                AgentData.Save(file, fields);
            }
            private void AddId(string name, string value)
            {
                if (CodexAuthMode.ValidRequestId(value, null))
                    fields[name] = value;
            }
            internal void Request(string id)
            {
                AddId("请求 ID", id);
                requests.Add(CodexAppServer.Obj("请求 ID", id, "用量", "未取得"));
            }

            internal void Response(string id, string model, AgentResult usage)
            {
                var round = (Dictionary<string, object>)requests[requests.Count - 1];
                round["响应 ID"] = id;
                round["返回模型"] = model;
                AddId("响应 ID", id);
                AddId("返回模型", model);
                if (usage.HasUsage)
                {
                    round["用量"] = "已取得";
                    round["输入 tokens"] = usage.Input;
                    round["缓存输入 tokens"] = usage.Cached;
                    round["输出 tokens"] = usage.Output;
                    if (usage.Reasoning > 0) round["推理 tokens"] = usage.Reasoning;
                }
                TrySave();
            }
            private void TrySave()
            {
                try { AgentData.Save(file, fields); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            internal void Finish(string status)
            {
                lock(fields)
                {
                    fields["状态"] = status;
                    fields["结束 UTC"] = DateTime.UtcNow.ToString("o");
                    TrySave();
                }
            }
        }

        private static AgentResult Execute(string exe, string work, string text, string model, string session,
            string schema, string output, IList<string> images, string project, string jobFolder,
            bool builtinImage, Action<AgentEvent> notify, CancellationToken cancel, bool forkSession,
            SelectedSkill skill, int timeoutMs, bool skillAudit, Func<bool> resultReady, Action<string> onDiagnostic, int idleTimeoutMs, bool assetImage, string effortOverride, string monitorProject)
        {
            if (builtinImage) effortOverride = "low";
            if (CodexAuthMode.IsApi)
            {
                if (jobFolder != null) throw new InvalidOperationException("外部媒体须由已确认的直接执行入口提交。");
                if (!skillAudit) SkillCompatibility.Ensure(null, model, skill, cancel);
                return OpenAiResponsesClient.Run(work, text, model, session, schema, output, images, project,
                    skill, skillAudit, builtinImage, null, effortOverride ?? AgentData.Options().ReasoningEffort, timeoutMs, notify, cancel);
            }
            if (String.IsNullOrEmpty(exe)) throw new InvalidOperationException("请在设置中连接 Codex 账号后继续。");
            if (!skillAudit) SkillCompatibility.Ensure(exe, model, skill, cancel);
            Directory.CreateDirectory(work);
            bool hasSession = !String.IsNullOrWhiteSpace(session);
            if (hasSession && !Regex.IsMatch(session, @"^[a-zA-Z0-9_-]+$"))
                throw new InvalidOperationException("会话编号无效。请新建对话。");
            var args = new List<string> { "exec" };
            if (hasSession) args.Add(forkSession ? "fork" : "resume");
            args.AddRange(new[] { "--json", "--ignore-user-config", "--skip-git-repo-check",
                "-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"",
                "--disable", "shell_tool" });
            if (!builtinImage && jobFolder == null && schema != null) args.AddRange(new[] { "-c", "web_search=\"disabled\"" });
            if (builtinImage) args.AddRange(new[] { "--enable", "image_generation" });
            else args.AddRange(new[] { "--disable", "image_generation" });
            if (schema != null)
                args.AddRange(new[] { "--ephemeral", "--output-schema", schema, "-o", output });
            if (builtinImage || jobFolder != null) args.Add("--ephemeral");
            if (jobFolder == null && (!builtinImage || assetImage || images != null && images.Count != 0))
                args.AddRange(CodexAuthMode.Arguments());
            if (ValidModel(model)) args.AddRange(new[] { "-m", model });
            string effort = effortOverride ?? AgentData.Options().ReasoningEffort;
            if (ValidEffort(effort) && (builtinImage || Models().Any(m => m.Key == (model ?? "") && m.Efforts.Contains(effort))))
                args.AddRange(new[] { "-c", "model_reasoning_effort=" + AgentData.Json().Serialize(effort) });
            var bridge = new List<string> { "--agent-mcp", project ?? "", jobFolder ?? "", work };
            if (skill != null)
            {
                if (!skill.PreloadedReferences || skillAudit) bridge.Add(skill.Directory);
                if (!skillAudit) text = "用户已为本轮明确选择本机 Skill「" + skill.Reference.Name + "」。"
                    + "按其与本轮任务相关的步骤处理，并遵守本轮指定的输出格式、工具权限和制作确认范围。"
                    + (skill.PreloadedReferences ? "引用规则已预载，无需调用工具。" : skill.Parts == null ? "如有引用文本，使用 read_selected_skill_file 读取相对路径。"
                        : "组合中用编号/相对路径读取引用文本，例如 2/references/rules.md。")
                    + "\n\n【所选 Skill】\n" + skill.Body + "\n【Skill 结束】\n\n【本轮任务】\n" + text;
            }
            var json = AgentData.Json();
            bool skillTool = bridge.Count > 4;
            bool headless = !builtinImage && jobFolder == null && schema != null && String.IsNullOrEmpty(project) && !skillTool;
            if (!headless && (!builtinImage || skillTool))
                args.AddRange(new[] { "-c", "mcp_servers.oneclick_film.command=" + json.Serialize(Assembly.GetExecutingAssembly().Location),
                "-c", "mcp_servers.oneclick_film.args=" + json.Serialize(bridge),
                "-c", "mcp_servers.oneclick_film.tool_timeout_sec=" + Math.Max(635, timeoutMs / 1000 + 5).ToString(CultureInfo.InvariantCulture) });
            if (jobFolder != null)
            {
                // The GUI already approved this exact request. All other media calls are unavailable.
                args.AddRange(new[] { "-c", "mcp_servers.oneclick_film.required=true",
                    "-c", "mcp_servers.oneclick_film.enabled_tools=[\"execute_approved_media\"]",
                    "-c", "mcp_servers.oneclick_film.tools.execute_approved_media.approval_mode=\"approve\"" });
            }
            else if (builtinImage && skillTool)
                args.AddRange(new[] { "-c", "mcp_servers.oneclick_film.enabled_tools=[\"read_selected_skill_file\"]" });
            else if (!builtinImage && !headless)
                args.AddRange(new[] { "-c", "mcp_servers.oneclick_film.tools.show_in_app.approval_mode=\"approve\"" });
            Action<string> sent = route => {
                AssetUsageLedger.Dispatch("Codex 账号", builtinImage);
                if (!FlowCreation.Development) return;
                string purpose = (DevMonitor.Operation ?? (builtinImage ? "内置出图" : "Codex 任务")) + " · " + Path.GetFileName(work);
                DevMonitor.CodexSend(monitorProject, purpose, route, model, effort, skill == null ? null : skill.Reference.Name,
                    text, schema, images == null ? 0 : images.Count);
            };
            if (!builtinImage && !assetImage && jobFolder==null && resultReady==null)
            {
                var server=CodexAppServer.Open(exe,cancel);
                if(server==null && onDiagnostic!=null)onDiagnostic("Codex app-server 建联未通过，改用 exec · "+CodexAppServer.OpenReason(exe));
                var choice=Models().FirstOrDefault(m=>m.Key==(model??""));
                var level=ValidEffort(effort) && choice!=null && choice.Efforts.Contains(effort) ? effort : choice==null?null:choice.DefaultEffort;
                var fast=server==null?null:server.TryTurn(Path.GetFullPath(work),text,model,level,session,forkSession,schema,output,images,bridge,
                    notify,cancel,timeoutMs>0?timeoutMs:180000,idleTimeoutMs,onDiagnostic,sent);
                if(fast!=null) { AssetUsageLedger.ModelResponse(fast); return fast; }
                cancel.ThrowIfCancellationRequested();
            }
            if(sent!=null)sent("exec");
            if(notify!=null)notify(new AgentEvent{Type="transport",Text="exec"});
            if (hasSession) args.Add(session);
            args.Add("-");
            foreach (string image in images ?? new List<string>())
                args.AddRange(new[] { "-i", image });

            // cmd shims cannot safely receive a user-selected configuration path with shell metacharacters.
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
                args.Any(a => Regex.IsMatch(a, @"[%!&|<>^]")))
                throw new InvalidOperationException("此配置包含命令行特殊字符。请选择原生 codex.exe 后再运行。");
            var result = new AgentResult { Session = forkSession ? null : session };
            string failure = null;
            bool done = false, collectedMedia = false;
            long completedAt = 0, lastEventAt = 0;
            bool reportedLock = false;
            Func<bool> readOutput = () =>
            {
                try { result.StructuredText = ReadStructuredFile(output); return true; }
                catch (IOException e)
                {
                    if (!SharingConflict(e)) throw;
                    if (!reportedLock && notify != null) notify(new AgentEvent { Type = "result_wait", Text = "结果文件被占用，正在等待读取…" });
                    reportedLock = true;
                    return false;
                }
            };
            var elapsed = Stopwatch.StartNew();
            var run = CodexConnection.Run(exe, Args(args), timeoutMs > 0 ? timeoutMs : (builtinImage || jobFolder != null ? 750000 : 180000),
                work, text, line =>
                {
                    if (idleTimeoutMs > 0 && line.TrimStart().StartsWith("{", StringComparison.Ordinal)) Interlocked.Exchange(ref lastEventAt, elapsed.ElapsedMilliseconds);
                    var e = Parse(line);
                    if (e == null) return;
                    switch (e.Type)
                    {
                        case "session": result.Session = e.Text; break;
                        case "message": result.Answer = e.Text; break;
                        case "step": result.Calls++; break;
                        case "complete":
                            result.HasUsage = e.HasUsage; result.Input = e.Input; result.Output = e.Output; result.Cached = e.Cached;
                            done = true;
                            Interlocked.Exchange(ref completedAt, elapsed.ElapsedMilliseconds + 1);
                            break;
                        case "failed": Interlocked.Exchange(ref failure, e.Text); break;
                    }
                    if (notify != null)
                    {
                        notify(e);
                        string locationFile = Path.Combine(work, AgentMcp.ChoiceFile);
                        if ((e.Type == "message" || e.Type == "complete") && File.Exists(locationFile))
                        {
                            try
                            {
                                var location = AgentData.Read<Dictionary<string, object>>(locationFile);
                                File.Delete(locationFile);
                                notify(new AgentEvent { Type = "navigate", Text = AgentData.Str(location, "location") });
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                }, cancel, () =>
                {
                    if (Volatile.Read(ref failure) != null) return true;
                    if (resultReady != null && resultReady()) { collectedMedia = true; return true; }
                    long terminal = Interlocked.Read(ref completedAt);
                    if (terminal == 0 && idleTimeoutMs > 0 && elapsed.ElapsedMilliseconds - Interlocked.Read(ref lastEventAt) >= idleTimeoutMs)
                    {
                        string phase = String.IsNullOrEmpty(result.Session) ? "尚未取得本机接单回执" : "已取得会话 " + result.Session;
                        Interlocked.Exchange(ref failure, phase + "，连续 " + (idleTimeoutMs / 1000) + " 秒无新事件。请在当前场次续做。"); return true;
                    }
                    if (terminal == 0 || builtinImage || jobFolder != null) return false;
                    // -o can be flushed slightly after the final event. Allow it to finish before reading.
                    if (schema == null) return true;
                    bool readable = readOutput();
                    if (result.StructuredText != null) return true;
                    if (!readable && elapsed.ElapsedMilliseconds - terminal >= 30000)
                        throw new IOException("结果文件持续被占用，本次结果尚未保存。请稍后续做。");
                    // Give a still-running writer time to flush before collecting a hung CLI.
                    return elapsed.ElapsedMilliseconds - terminal >= 30000;
                }, onDiagnostic, codexProvider:true);
            AssetUsageLedger.ModelResponse(result);
            if (run.OutputRecovered && collectedMedia && Volatile.Read(ref failure) == null) return result;
            result.TransportNote = run.Error.Split('\n').Reverse()
                .FirstOrDefault(x => x.IndexOf("MCP", StringComparison.OrdinalIgnoreCase) >= 0);
            if (run.Code != 0 || !done || failure != null)
            {
                string detail = failure ?? run.Error.Trim();
                string low = detail.ToLowerInvariant();
                if(CodexAuthMode.IsApi)detail=CodexAuthMode.SafeFailure(detail);
                else if (low.Contains("401") || low.Contains("unauthorized") || low.Contains("not logged in"))
                    detail = "Codex 登录已失效。请重新登录。";
                else if (low.Contains("429") || low.Contains("rate limit") || low.Contains("quota"))
                    detail = "Codex 达到使用限制。请检查账号额度。";
                else if (low.Contains("failed to connect") || low.Contains("error sending request") ||
                    low.Contains("connection refused") || low.Contains("dns"))
                    detail = "Codex 请求未连通。请检查互联网连接。";
                throw new InvalidOperationException(AgentData.Short(String.IsNullOrWhiteSpace(detail)
                    ? "Codex 未报告完整结果。" : detail, 700));
            }
            if (schema != null && result.StructuredText == null)
            {
                var wait = Stopwatch.StartNew();
                while (!readOutput())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (wait.ElapsedMilliseconds >= 30000)
                        throw new IOException("结果文件仍被占用，本次结果尚未保存。请释放文件后续做。");
                    cancel.WaitHandle.WaitOne(200);
                }
            }
            if(schema!=null && result.StructuredText == null)
            {
                IDictionary<string,object> final=null;
                if(!String.IsNullOrWhiteSpace(result.Answer))
                {
                    try { final=AgentData.Json().DeserializeObject(result.Answer) as IDictionary<string,object>; }
                    catch(ArgumentException){} catch(InvalidOperationException){}
                }
                if(final==null) throw new InvalidDataException("Codex 已结束，但未交回有效的结构化结果。会话 "+result.Session);
                AgentData.Save(output,final);
                result.StructuredText = result.Answer;
            }
            if (schema == null && !builtinImage && jobFolder == null && String.IsNullOrWhiteSpace(result.Answer))
                throw new InvalidOperationException("Codex 未返回可显示的回答。");
            if (forkSession && String.IsNullOrWhiteSpace(result.Session))
                throw new InvalidOperationException("Codex 未返回续接后的会话编号。");
            return result;
        }

        internal static AgentEvent Parse(string line)
        {
            if (String.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return null;
            IDictionary<string, object> record;
            try { record = AgentData.Json().Deserialize<Dictionary<string, object>>(line); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            var item = AgentData.Value(record, "item") as IDictionary<string, object>;
            string kind = AgentData.Str(item, "type");
            switch (AgentData.Str(record, "type"))
            {
                case "thread.started": return new AgentEvent { Type = "session", Text = AgentData.Str(record, "thread_id"), ReportedModel = AgentData.Str(record, "model") };
                case "turn.started": return new AgentEvent { Type = "started", ReportedModel = AgentData.Str(record, "model"), RequestId = AgentData.Str(record, "request_id") };
                case "error":
                    string detail = AgentData.Str(record, "message");
                    if (detail.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase))
                        return new AgentEvent { Type = "retry", Text = Regex.Match(detail, @"\b\d+/\d+\b").Value };
                    break;
                case "item.started":
                    if (kind == "reasoning") return new AgentEvent { Type = "reasoning" };
                    if (kind == "image_generation" || kind == "image_generation_call" || kind == "imageGeneration")
                        return new AgentEvent { Type = "image_started" };
                    if (kind == "mcp_tool_call") return new AgentEvent { Type = "step", Text = AgentData.Str(item, "tool") };
                    break;
                case "item.completed":
                    if (kind == "error") return new AgentEvent { Type = "notice", Text = AgentData.Str(item, "message") };
                    if (kind == "image_generation" || kind == "image_generation_call" || kind == "imageGeneration")
                    {
                        var cause = AgentData.Value(item, "failure") as IDictionary<string, object>;
                        if (AgentData.Str(item, "status") == "failed" || cause != null)
                            return new AgentEvent { Type = "failed", Text = AgentData.Str(cause, "type") == "usageLimitExceeded"
                                ? "图像生成用量达到平台限额。" : "图像生成工具报告本次失败。" };
                        return new AgentEvent { Type = "image_finished" };
                    }
                    if (kind == "agent_message") return new AgentEvent { Type = "message", Text = AgentData.Str(item, "text") };
                    if (kind == "mcp_tool_call" && AgentData.Str(item, "status") == "failed")
                        return new AgentEvent { Type = "tool_error", Text = AgentData.Str(item, "tool") };
                    break;
                case "turn.completed":
                    var tokens = AgentData.Value(record, "usage") as IDictionary<string, object>;
                    return new AgentEvent { Type = "complete", HasUsage = tokens != null && tokens.ContainsKey("input_tokens") && tokens.ContainsKey("output_tokens"), RequestId = AgentData.Str(record, "request_id"),
                        ReportedModel = AgentData.Str(record, "model"), Input = AgentData.Number(tokens, "input_tokens"),
                        Cached = AgentData.Number(tokens, "cached_input_tokens"), Output = AgentData.Number(tokens, "output_tokens") };
                case "turn.failed":
                    string error = AgentData.Str(AgentData.Value(record, "error") as IDictionary<string, object>, "message");
                    return new AgentEvent { Type = "failed", RequestId = AgentData.Str(record,"request_id"), Text = error.Length == 0 ? "Codex 执行失败。" : error };
            }
            return null;
        }
    }
}



