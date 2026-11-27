using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneClickFilm
{
    public sealed class MediaRequest
    {
        public string Kind { get; set; }
        public string Route { get; set; }
        public string Prompt { get; set; }
        public AgentOptions Options { get; set; }
        public int TimeoutMs { get; set; }
        public string AssetReservationId { get; set; }
        public string ReferencePath { get; set; }
        public string TargetSize { get; set; }
        public string ExternalId { get; set; }
    }

    public sealed class MediaReceipt
    {
        public string ExternalId { get; set; }
        public string Output { get; set; }
        public string State { get; set; }
    }

    public sealed class SpendEntry
    {
        public string Job { get; set; }
        public string Day { get; set; }
        public decimal? Cost { get; set; }
    }

    internal static class AgentMedia
    {
        internal const string ReceiptFile = "receipt.json";
        internal const string RequestFile = "request.json";
        internal const string PreflightFailureFile = "preflight.txt";
        internal const string BuiltinSessionFile = "builtin-session.txt";
        internal const string SubmittedPromptFile = "submitted-prompt.txt";

        [DllImport("kernel32.dll")]
        internal static extern uint SetThreadExecutionState(uint flags);

        internal static string Route(AgentOptions options, string kind)
        {
            return kind == "图片" ? options.ImageRoute : options.VideoRoute;
        }

        internal static string RouteName(string route)
        {
            return FlowChannels.IsResponsesApi(route) ? FlowChannels.ResponsesApiName : route == "builtin" ? (CodexAuthMode.IsApi ? "Responses API 内置生图" : "Codex 内置生图") : route == "api" ? "外部视频 API" : "外部平台 CLI";
        }

        private static decimal? Amount(string value, string field)
        {
            decimal money;
            if (String.IsNullOrWhiteSpace(value)) return null;
            if (!Decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out money) || money < 0 || money > 1000000)
                throw new InvalidOperationException(field + "应填写 0 到 1000000 之间的金额。");
            return money;
        }

        internal static decimal? Cost(AgentOptions options, string kind)
        {
            return Amount(kind == "图片" ? options.ImageCost : options.VideoCost, "单次预估价");
        }

        private static string LedgerPath
        {
            get { return Path.Combine(AgentData.Local, "Codex费用预留.json"); }
        }

        internal static void Budget(AgentOptions options, string kind, int count, string reserveJob = null)
        {
            var cost = Cost(options, kind);
            var maximum = Amount(options.PerTaskLimit, "单次限额");
            var daily = Amount(options.DailyLimit, "当日限额");
            string day = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(AgentData.Local);
            using (FileStream gate = OpenSpendLedger())
            {
                var entries = AgentData.Read<List<SpendEntry>>(LedgerPath) ?? new List<SpendEntry>();
                if (reserveJob != null && entries.Any(x => x.Job == reserveJob))
                    throw new InvalidOperationException("该任务已经预留费用。请核对原任务。");
                if ((!cost.HasValue && (maximum.HasValue || daily.HasValue)) ||
                    (maximum.HasValue && cost.HasValue && cost.Value > maximum.Value) ||
                    (daily.HasValue && (entries.Any(x => x.Day == day && !x.Cost.HasValue) ||
                    entries.Where(x => x.Day == day).Sum(x => x.Cost ?? 0m) + (cost ?? 0m) * count > daily.Value)))
                    throw new InvalidOperationException("当前预估费用或今日未知费用超出你设置的限额。请核对报价与设置。");
                if (reserveJob != null)
                {
                    entries.Add(new SpendEntry { Job = reserveJob, Day = day, Cost = cost });
                    AgentData.Save(LedgerPath, entries);
                }
            }
        }

        private static FileStream OpenSpendLedger()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(LedgerPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException)
                {
                    if (attempt >= 100) throw;
                    Thread.Sleep(25);
                }
            }
        }

        private static Uri Endpoint(string text)
        {
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) ||
                (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")) ||
                !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("API 与产物链接须为 HTTPS，或者本机测试 HTTP 地址。");
            return uri;
        }

        internal static void Validate(AgentOptions o, string kind, string route)
        {
            Cost(o, kind);
            if (FlowChannels.IsNative(route) && kind == "图片") return;
            if (route == "cli")
            {
                string exe = kind == "图片" ? o.ImageCli : o.VideoCli;
                string args = kind == "图片" ? o.ImageArgs : o.VideoArgs;
                if (String.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe) || !File.Exists(exe) ||
                    !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请在渠道设置选择平台 CLI 的 .exe 程序。");
                if (kind == "图片" && o.ImageDriver == FlowChannels.Dreamina) return;
                if (String.IsNullOrWhiteSpace(args) || !args.Contains("{output}"))
                    throw new InvalidOperationException("CLI 参数必须包含 {output}，用于找回本次产物。");
                return;
            }
            if (route != "api" || kind != "视频")
                throw new InvalidOperationException("请选择可用于当前媒体类型的 Codex 渠道。");
            Endpoint(o.SubmitUrl);
            if (!String.IsNullOrWhiteSpace(o.StatusUrl))
            {
                if (!o.StatusUrl.Contains("{id}") || String.IsNullOrWhiteSpace(o.IdField))
                    throw new InvalidOperationException("异步 API 需要填写任务号字段，查询地址需要包含 {id}。");
                Endpoint(o.StatusUrl.Replace("{id}", "test"));
            }
            if (String.IsNullOrWhiteSpace(o.ResultField))
                throw new InvalidOperationException("请填写返回视频地址的 JSON 字段。");
            if (!String.IsNullOrWhiteSpace(o.TokenVariable) &&
                (!Regex.IsMatch(o.TokenVariable, @"^[A-Za-z_][A-Za-z0-9_]*$") ||
                 String.IsNullOrEmpty(Environment.GetEnvironmentVariable(o.TokenVariable))))
                throw new InvalidOperationException("找不到指定环境变量中的平台令牌。设置后重新打开软件。");
            if (!String.IsNullOrWhiteSpace(o.ApiBody) &&
                !(AgentData.Json().DeserializeObject(o.ApiBody) is IDictionary<string, object>))
                throw new InvalidOperationException("视频 API 请求模板应为 JSON 对象。");
        }

        private static object RenderTemplate(object obj, string prompt)
        {
            var map = obj as IDictionary<string, object>;
            if (map != null) return map.ToDictionary(x => x.Key, x => RenderTemplate(x.Value, prompt));
            var array = obj as IList;
            if (array != null) return array.Cast<object>().Select(x => RenderTemplate(x, prompt)).ToArray();
            return obj is string ? ((string)obj).Replace("{prompt}", prompt) : obj;
        }

        private static string Field(object data, string selector)
        {
            if (String.IsNullOrWhiteSpace(selector)) return "";
            foreach (string part in selector.Split('.'))
            {
                var map = data as IDictionary<string, object>;
                var array = data as IList;
                int index;
                if (map != null) data = AgentData.Value(map, part);
                else if (array != null && Int32.TryParse(part, out index) && index >= 0 && index < array.Count)
                    data = array[index];
                else return "";
            }
            return data is string || data is int || data is long ? Convert.ToString(data, CultureInfo.InvariantCulture) : "";
        }

        private static HttpWebRequest Request(Uri uri, string verb, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = verb;
            request.AllowAutoRedirect = false;
            if (uri.IsLoopback) request.Proxy = null;
            else ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            request.Timeout = 25000;
            request.ReadWriteTimeout = 25000;
            if (!String.IsNullOrEmpty(token)) request.Headers["Authorization"] = "Bearer " + token;
            return request;
        }

        private sealed class TransientVideoError : InvalidOperationException
        {
            internal TransientVideoError(string detail) : base(detail) { }
        }

        private static object JsonRequest(Uri uri, string verb, string token, object body, CancellationToken cancel)
        {
            var request = Request(uri, verb, token);
            using (cancel.Register(request.Abort))
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(AgentData.Json().Serialize(body));
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                        throw new InvalidOperationException("平台 API 返回 HTTP " + (int)response.StatusCode + "。");
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var buffer = new char[1024 * 1024 + 1];
                        int length = reader.ReadBlock(buffer, 0, buffer.Length);
                        if (length == buffer.Length) throw new InvalidOperationException("平台状态响应超过 1 MB。");
                        return AgentData.Json().DeserializeObject(new string(buffer, 0, length));
                    }
                }
            }
            catch (WebException error)
            {
                cancel.ThrowIfCancellationRequested();
                using (var response = error.Response as HttpWebResponse)
                {
                    if (response != null)
                    {
                        int code = (int)response.StatusCode;
                        string detail = AgentData.Str(CodexAuthMode.ReadError(response), "message");
                        string message = "平台 API 返回 HTTP " + code + " · " + AgentData.Short(FlowAutoRepair.Safe(CodexAuthMode.RedactTestMessage(detail,token)),420);
                        if (code >= 500) throw new TransientVideoError(message);
                        throw new InvalidOperationException(message);
                    }
                    string reason = "平台 API 网络连接失败 · " + error.Status;
                    if (OpenAiResponsesClient.CanRetry(error.Status)) throw new TransientVideoError(reason);
                    throw new InvalidOperationException(reason);
                }
            }
            catch (IOException)
            {
                cancel.ThrowIfCancellationRequested();
                throw new TransientVideoError("平台 API 网络读写中断。");
            }
        }

        private static MediaReceipt VideoApi(MediaRequest r, string folder, MediaReceipt receipt, CancellationToken cancel)
        {
            var o = r.Options;
            string token = String.IsNullOrWhiteSpace(o.TokenVariable) ? null : Environment.GetEnvironmentVariable(o.TokenVariable);
            string url = "";
            if (String.IsNullOrWhiteSpace(r.ExternalId))
            {
                object body = String.IsNullOrWhiteSpace(o.ApiBody) ? new { prompt = r.Prompt } :
                    RenderTemplate(AgentData.Json().DeserializeObject(o.ApiBody), r.Prompt);
                object submitted = JsonRequest(Endpoint(o.SubmitUrl), "POST", token, body, cancel);
                receipt.ExternalId = Field(submitted, o.IdField);
                url = Field(submitted, o.ResultField);
                AgentData.Save(Path.Combine(folder, ReceiptFile), receipt);
            }
            else receipt.ExternalId = r.ExternalId;
            if (url.Length == 0 && !String.IsNullOrWhiteSpace(receipt.ExternalId) && !String.IsNullOrWhiteSpace(o.StatusUrl))
            {
                Uri address = Endpoint(o.StatusUrl.Replace("{id}", Uri.EscapeDataString(receipt.ExternalId)));
                if (address.GetLeftPart(UriPartial.Authority) != Endpoint(o.SubmitUrl).GetLeftPart(UriPartial.Authority))
                    throw new InvalidOperationException("提交与状态查询必须属于同一个平台地址。");
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int transientFailures = 0;
                while (timer.ElapsedMilliseconds < 525000)
                {
                    object status;
                    try
                    {
                        status = JsonRequest(address, "GET", token, null, cancel);
                        transientFailures = 0;
                    }
                    catch (TransientVideoError)
                    {
                        if (++transientFailures > 3) throw;
                        if (cancel.WaitHandle.WaitOne(2500)) cancel.ThrowIfCancellationRequested();
                        continue;
                    }
                    string state = Field(status, o.StateField);
                    if (!String.IsNullOrEmpty(o.FailureState) && state.Equals(o.FailureState, StringComparison.OrdinalIgnoreCase))
                    {
                        receipt.State = "平台已失败";
                        AgentData.Save(Path.Combine(folder, ReceiptFile), receipt);
                        throw new InvalidOperationException("视频平台报告制作失败。");
                    }
                    if (String.IsNullOrEmpty(o.SuccessState) || state.Equals(o.SuccessState, StringComparison.OrdinalIgnoreCase))
                        url = Field(status, o.ResultField);
                    if (url.Length > 0) break;
                    if (cancel.WaitHandle.WaitOne(2500)) cancel.ThrowIfCancellationRequested();
                }
            }
            if (url.Length == 0)
                throw new InvalidOperationException("暂未获得视频地址。若已记录平台任务号，可继续核对原任务。");
            string finalFile = Path.Combine(folder, "output.mp4");
            string tempFile = Path.Combine(folder, "output.mp4.part");
            try
            {
                Uri address = Endpoint(url);
                for (int redirects = 0; ; redirects++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var download = Request(address, "GET", null);
                    using (cancel.Register(download.Abort))
                    using (var response = (HttpWebResponse)download.GetResponse())
                    {
                        int code = (int)response.StatusCode;
                        if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                        {
                            Uri next;
                            string location = response.Headers["Location"];
                            if (redirects >= 3 || String.IsNullOrWhiteSpace(location) || !Uri.TryCreate(address, location, out next))
                                throw new InvalidOperationException("视频下载重定向无效或超过 3 次。");
                            address = Endpoint(next.AbsoluteUri);
                            continue;
                        }
                        if (code < 200 || code >= 300) throw new InvalidOperationException("视频下载返回 HTTP " + code + "。");
                        using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (Stream remote = response.GetResponseStream())
                        {
                            byte[] buffer = new byte[64 * 1024];
                            int n;
                            while ((n = remote.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (output.Length + n > 1024L * 1024 * 1024)
                                    throw new InvalidOperationException("单个视频超过 1 GB。");
                                output.Write(buffer, 0, n);
                            }
                        }
                        break;
                    }
                }
                if (File.Exists(finalFile)) File.Replace(tempFile, finalFile, null);
                else File.Move(tempFile, finalFile);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            receipt.Output = "output.mp4";
            return receipt;
        }

        internal static MediaReceipt Execute(string folder)
        {
            return Execute(folder, CancellationToken.None);
        }

        internal static MediaReceipt Execute(string folder, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var r = AgentData.Read<MediaRequest>(Path.Combine(folder, RequestFile));
            if (r == null || r.Options == null) throw new InvalidOperationException("媒体任务授权文件缺失。");
            Validate(r.Options, r.Kind, r.Route);
            string marker = Path.Combine(folder, "invoked");
            if (!String.IsNullOrEmpty(r.AssetReservationId))
            {
                if (r.Kind != "图片" || r.Route != "cli" || !AgentData.ValidId(r.AssetReservationId) ||
                    !String.Equals(Path.GetFileName(folder), r.AssetReservationId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("外部资产出图任务编号无效。");
                using (new FileStream(Path.Combine(folder, "dispatch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                    if (File.Exists(marker)) throw new InvalidOperationException("本次资产任务已提交。");
                    try
                    {
                        AssetChannels.CheckCli(r.Options, Path.GetDirectoryName(r.Options.ImageCli), cancel);
                    }
                    catch (Exception e)
                    {
                        File.WriteAllText(Path.Combine(folder, PreflightFailureFile), AgentData.Short(e.Message, 300));
                        throw;
                    }
                    cancel.ThrowIfCancellationRequested();
                    Budget(r.Options, r.Kind, 1, r.AssetReservationId);
                    return Submit(r, folder, marker, cancel);
                }
            }
            return Submit(r, folder, marker, cancel);
        }

        private static MediaReceipt Submit(MediaRequest r, string folder, string marker, CancellationToken cancel)
        {
            using (File.Open(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var receipt = new MediaReceipt { State = "已发起", ExternalId = r.ExternalId };
            AgentData.Save(Path.Combine(folder, ReceiptFile), receipt);
            if (r.Kind == "图片" && r.Route == "cli") AssetUsageLedger.Dispatch("外部平台");
            if (r.Route == "api")
                receipt = VideoApi(r, folder, receipt, cancel);
            else if (r.Kind == "图片" && r.Options.ImageDriver == FlowChannels.Dreamina)
                FlowChannels.SubmitDreamina(r, folder, receipt, cancel);
            else
            {
                bool picture = r.Kind == "图片";
                string output = Path.Combine(folder, picture ? "output.png" : "output.mp4");
                string input = Path.Combine(folder, "prompt.txt");
                File.WriteAllText(input, r.Prompt, new UTF8Encoding(false));
                var arguments = CliArguments(picture ? r.Options.ImageArgs : r.Options.VideoArgs, r.ReferencePath, r.TargetSize)
                    .Select(a => a.Replace("{output}", output).Replace("{prompt_file}", input).Replace("{prompt}", r.Prompt));
                bool asset = picture && r.AssetReservationId != null;
                int deadline = asset && r.TimeoutMs >= 120000 && r.TimeoutMs <= 1800000 ? r.TimeoutMs : 540000;
                var candidate = new StableImage();
                var run = CodexConnection.Run(picture ? r.Options.ImageCli : r.Options.VideoCli,
                    AgentRunner.Args(arguments), deadline, folder, cancellation: cancel, resultReady: asset ? (Func<bool>)(() => candidate.Check(output)) : null);
                if (run.Code != 0 || !File.Exists(output))
                    throw new InvalidOperationException("外部 CLI 未在指定位置交回产物，退出码 " + run.Code + "。");
                receipt.Output = Path.GetFileName(output);
            }
            ValidateOutput(AgentData.Inside(folder, receipt.Output), r.Kind);
            receipt.State = "已取回";
            AgentData.Save(Path.Combine(folder, ReceiptFile), receipt);
            return receipt;
        }

        internal static IEnumerable<string> CliArguments(string template, string reference, string size)
        {
            var sides = ImageOutputSettings.IsValid(size) ? size.Split('x') : null;
            var lines = template.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string arg = lines[i];
                if (String.IsNullOrEmpty(reference))
                {
                    if (arg.StartsWith("-", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1] == "{reference}")
                    {
                        i++;
                        continue;
                    }
                    if (arg.Contains("{reference}")) continue;
                }
                if (sides == null)
                {
                    if (arg.StartsWith("-", StringComparison.Ordinal) && i + 1 < lines.Length &&
                        (lines[i + 1] == "{width}" || lines[i + 1] == "{height}"))
                    {
                        i++;
                        continue;
                    }
                    if (arg.Contains("{width}") || arg.Contains("{height}")) continue;
                }
                else arg = arg.Replace("{width}", sides[0]).Replace("{height}", sides[1]);
                yield return arg.Replace("{reference}", reference ?? "");
            }
        }

        internal static string JobFolder(string project, AgentJob job)
        {
            if (!AgentData.ValidId(job.Id)) throw new InvalidOperationException("制作任务编号无效。");
            return AgentData.Inside(AgentData.DirectoryFor(project), Path.Combine("运行收据", job.Id));
        }

        internal static string AttemptFolder(string jobFolder, string checkId)
        {
            if (checkId == null) return jobFolder;
            if (!AgentData.ValidId(checkId)) throw new InvalidOperationException("制作核对编号无效。");
            return AgentData.Inside(jobFolder, "核对-" + checkId);
        }

        internal static MediaReceipt ReadJobReceipt(string jobFolder, string checkId)
        {
            return AgentData.Read<MediaReceipt>(AgentData.Inside(AttemptFolder(jobFolder, checkId), ReceiptFile));
        }

        internal static bool ConfirmedFailure(string folder)
        {
            try
            {
                var receipt = ReadJobReceipt(folder, null);
                return receipt != null && receipt.State == "平台已失败";
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        internal static void ValidateOutput(string file, string kind, int expectedSeconds=0,string ratio=null,string resolution=null)
        {
            if (!File.Exists(file) || new FileInfo(file).Length < 12)
                throw new InvalidOperationException("生成渠道未交回完整文件。");
            if (kind == "视频")
            {
                string probe = CodexConnection.FindOnPath("ffprobe.exe") ??
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\ffmpeg\bin\ffprobe.exe");
                if (!File.Exists(probe)) throw new InvalidOperationException("找到视频，请安装 FFprobe 后核对视频轨和时长。");
                var result = CodexConnection.Run(probe, AgentRunner.Args(new[] { "-v", "error", "-select_streams",
                    "v:0", "-show_entries", "stream=codec_type,width,height:format=duration,format_name", "-of", "json", file }), 15000);
                if (result.Code != 0 || String.IsNullOrWhiteSpace(result.Output))
                    throw new InvalidOperationException("产物无法核验为含视频轨、且不超过 30 秒的视频。");
                object json;
                try { json = AgentData.Json().DeserializeObject(result.Output); }
                catch (ArgumentException) { throw new InvalidOperationException("FFprobe 返回格式无法识别，无法核验视频。"); }
                catch (InvalidOperationException) { throw new InvalidOperationException("FFprobe 返回格式无法识别，无法核验视频。"); }
                double seconds;
                if (Field(json, "streams.0.codec_type") != "video" ||
                    !Field(json, "format.format_name").Split(',').Contains("mp4") ||
                    !Double.TryParse(Field(json, "format.duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                    seconds <= 0 || seconds > (expectedSeconds>0?30.85:30.5))
                    throw new InvalidOperationException("产物无法核验为含视频轨、且不超过 30 秒的视频。");
                if(expectedSeconds>0)
                {
                    int width,height;
                    string[] sides=(ratio??"").Split(':');
                    int w,h;
                    if(!Int32.TryParse(Field(json,"streams.0.width"),out width) || !Int32.TryParse(Field(json,"streams.0.height"),out height) ||
                        sides.Length!=2 || !Int32.TryParse(sides[0],out w) || !Int32.TryParse(sides[1],out h) || height<=0 || w<=0 || h<=0 ||
                        Math.Abs(seconds-expectedSeconds)>0.85 || Math.Abs((double)width/height*h/w-1)>0.04)
                        throw new InvalidDataException("回片时长或画幅与提交参数不符，请核对平台任务。");
                    int pixels;
                    if((resolution=="4k" && Math.Max(width,height)<3800) ||
                        (System.Text.RegularExpressions.Regex.IsMatch(resolution??"", @"^\d+p$") && Int32.TryParse(resolution.TrimEnd('p'),out pixels) && Math.Min(width,height)<pixels*0.96))
                        throw new InvalidDataException("平台回片低于选定的分辨率。");
                }
            }
            else
            {
                using (var stream = File.OpenRead(file))
                using (var reader = new BinaryReader(stream))
                {
                    byte[] header = reader.ReadBytes(12);
                    bool png = header.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                    bool jpg = header[0] == 255 && header[1] == 216 && header[2] == 255;
                    bool webp = Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
                    if (!png && !jpg && !webp) throw new InvalidOperationException("图片格式校验失败。");
                    string expected = png ? ".png" : jpg ? ".jpg" : ".webp";
                    if (Path.GetExtension(file).ToLowerInvariant() != expected && !(jpg && Path.GetExtension(file).ToLowerInvariant() == ".jpeg"))
                        throw new InvalidOperationException("图片实际格式与扩展名不一致。");
                    if (!webp)
                    {
                        stream.Position = 0;
                        using (var image = System.Drawing.Image.FromStream(stream, true, true))
                            if (image.Width < 1 || image.Height < 1)
                                throw new InvalidOperationException("图片无法解码。");
                    }
                    else if (BitConverter.ToUInt32(header, 4) + 8L > stream.Length)
                        throw new InvalidOperationException("WebP 文件内容不完整。");
                }
            }
        }

        internal sealed class StableImage
        {
            private string previousPath;
            private long previousSize, previousWrite;

            internal bool Check(string path)
            {
                if (path == null || !File.Exists(path)) { previousPath = null; return false; }
                try
                {
                    var info = new FileInfo(path);
                    long size = info.Length, write = info.LastWriteTimeUtc.Ticks;
                    if (previousPath == path && previousSize == size && previousWrite == write)
                    {
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                            AssetProduction.Inspect(path, new AssetAttempt());
                        return true;
                    }
                    previousPath = path;
                    previousSize = size;
                    previousWrite = write;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
                catch (FormatException) { }
                catch (NotSupportedException) { }
                catch (InvalidOperationException) { }
                catch (System.Runtime.InteropServices.COMException) { }
                return false;
            }
        }

        internal static string BuiltinOutput(string session, bool exactlyOne = false)
        {
            string output = FindBuiltinOutput(session, exactlyOne);
            if (output == null) throw new InvalidOperationException("找不到唯一对应本次 Codex 会话的图片。");
            return output;
        }

        internal static string FindBuiltinOutput(string session, bool exactlyOne)
        {
            var outputs = BuiltinCandidates(session);
            return outputs.Length == 0 || (exactlyOne && outputs.Length != 1) ? null : outputs[0];
        }

        internal static string[] BuiltinCandidates(string session)
        {
            if (String.IsNullOrWhiteSpace(session) || !Regex.IsMatch(session, @"^[a-zA-Z0-9_-]+$"))
                throw new InvalidOperationException("Codex 未返回可查找图片的会话编号。");
            string root = AgentData.Inside(AgentRunner.CodexHome, Path.Combine("generated_images", session));
            return Directory.Exists(root) ? Directory.EnumerateFiles(root)
                .Where(p => new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .OrderByDescending(File.GetLastWriteTimeUtc).Select(p => AgentData.Inside(root, Path.GetFileName(p))).ToArray() : new string[0];
        }
    }
}
