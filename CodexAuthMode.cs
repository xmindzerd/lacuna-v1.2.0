using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using System.Text;
using System.Collections.Generic;

namespace OneClickFilm
{
    public sealed class CodexConnectionPreferences
    {
        public string Mode { get; set; }
        public string BaseUrl { get; set; }
        public string Model { get; set; }
    }

    internal sealed class CodexApiModels
    {
        internal readonly string[] Ids;
        private readonly string receipt;

        internal CodexApiModels(string[] ids, string receipt)
        {
            Ids = ids;
            this.receipt = receipt;
        }

        internal string Describe(string model)
        {
            string selection = String.IsNullOrWhiteSpace(model) ? "" :
                " · " + model + (Ids.Contains(model, StringComparer.Ordinal) ? " 已列出" : " 未列出");
            return (Ids.Length == 0 ? "成功 · 列表为空" : "成功 · " + Ids.Length + " 个模型") + " · " + receipt + selection;
        }
    }

    internal static class CodexAuthMode
    {
        internal const string DefaultUrl = "https://api.openai.com/v1";
        internal const string Variable = "LACUNA_CODEX_API_KEY";
        private static string launchKey;
        private static string activeKey;
        private static volatile CodexConnectionPreferences current;
        private static string FileName { get { return Path.Combine(AgentData.Local,"Codex连接方式-patch2.json"); } }
        internal static void EnsureBuiltinImage()
        {
            if (IsApi) EnsureResponsesApi();
        }
        internal static bool ResponsesApiReady(out string reason)
        {
            try
            {
                Preflight(BaseUrl, Model);
                if (!HasKey) throw new InvalidOperationException("缺少项目 Key。");
                reason = "模型 " + Model + " · " + BaseUrl;
                return true;
            }
            catch (Exception e)
            {
                if (!(e is ArgumentException || e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException)) throw;
                reason = e.Message + " 请前往「设置 → Codex 连接方式」保存项目 Key 与 API 模型 ID。";
                return false;
            }
        }
        internal static void EnsureResponsesApi()
        {
            Preflight(BaseUrl, Model);
            ResolveApiKey();
        }
        internal static void Initialize()
        {
            launchKey = Environment.GetEnvironmentVariable("LACUNA_OPENAI_API_KEY");
            Environment.SetEnvironmentVariable("LACUNA_OPENAI_API_KEY",null);
            Environment.SetEnvironmentVariable(Variable,null);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (Enum.IsDefined(typeof(SecurityProtocolType),"Tls13"))
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)Enum.Parse(typeof(SecurityProtocolType),"Tls13");
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = Math.Max(8,ServicePointManager.DefaultConnectionLimit);
        }
        internal const string PreferenceWarning = "连接方式偏好文件已损坏，请在设置中重新应用连接设置。";
        private static readonly object preferenceLock = new object();
        private static bool corrupt;
        internal static bool PreferenceCorrupt { get { return Current != null && corrupt; } }
        private static CodexConnectionPreferences Current
        {
            get
            {
                if (current != null) return current;
                lock (preferenceLock)
                {
                    if (current != null) return current;
                    var path = FileName;
                    CodexConnectionPreferences saved = null;
                    try { saved = AgentData.Read<CodexConnectionPreferences>(path); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    corrupt = saved == null ? File.Exists(path) : saved.Mode != "api" && saved.Mode != "account";
                    current = saved != null && !corrupt ? saved : new CodexConnectionPreferences { Mode = "account" };
                    return current;
                }
            }
        }
        internal static bool IsApi { get { return Current.Mode == "api"; } }
        internal static string Label { get { return IsApi ? "Responses API" : "Codex · 账号"; } }
        internal static string Model { get { return Current.Model ?? ""; } }
        internal static string BaseUrl { get { return Current.BaseUrl ?? DefaultUrl; } }
        internal static string EffectiveModel(string selected)
        {
            return IsApi ? Model : String.IsNullOrWhiteSpace(selected) ? AgentRunner.ConfiguredModel() : selected;
        }
        internal static bool HasLaunchKey { get { return !String.IsNullOrWhiteSpace(launchKey); } }
        internal static bool HasKey { get { return HasLaunchKey || AnalysisKeyStore.HasSavedKey; } }
        internal static string ResolveApiKey()
        {
            var key = HasLaunchKey ? launchKey.Trim() : AnalysisKeyStore.Read();
            if(String.IsNullOrEmpty(key)) throw new InvalidOperationException("API 模式缺少项目 Key，请在设置中保存。");
            activeKey=key;
            return key;
        }
        internal static void ClearActiveKey() { Interlocked.Exchange(ref activeKey, null); }
        internal static string Redact(string text)
        {
            var key=activeKey;
            return text==null || String.IsNullOrEmpty(key) ? text : text.Replace(key,"[已隐藏]");
        }
        internal static string RedactDiagnostic(string text)
        {
            text=Redact(text)??"";
            text=Regex.Replace(text,@"(?i)data:image/[a-z0-9.+-]+;base64,[a-z0-9+/=]+","[图片数据已隐藏]");
            text=Regex.Replace(text,@"(?im)\b(Set-Cookie|Cookie)\s*:[^\r\n]+","$1: [已隐藏]");
            text=Regex.Replace(text,@"(?i)\bBearer\s+[^\s,;""']+","Bearer [已隐藏]");
            text=Regex.Replace(text,@"(?i)([""']?(?:[\w-]*api[_-]?key|access[_-]?token|authorization)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;}]+)","$1[已隐藏]");
            return Regex.Replace(text,@"(?i)\bsk-[a-z0-9_-]+","[已隐藏]");
        }
        internal static string RedactTestMessage(string text,string key)
        {
            string secret=(key??"").Trim();
            return RedactDiagnostic(String.IsNullOrEmpty(secret) || text==null ? text : text.Replace(secret,"[已隐藏]"));
        }
        internal static string SafeFailure(string raw)
        {
            string text=Redact(raw)??"";
            var status=Regex.Match(text,@"\b(401|403|429|404|400|5\d\d)\b");
            if(status.Success)
            {
                string hint=status.Value=="401" || status.Value=="403" ? "，请检查项目 Key 与模型权限" :
                    status.Value=="429" ? "，请检查组织额度与速率限制" : "";
                return "API 请求失败 · HTTP "+status.Value+hint;
            }
            string low=text.ToLowerInvariant();
            if(low.Contains("quota") || low.Contains("rate limit"))return "API 请求受限，请检查组织额度与速率限制。";
            if(low.Contains("unauthorized") || low.Contains("forbidden"))return "API 鉴权失败，请检查项目 Key 与模型权限。";
            if(text.Contains("Key") || text.Contains("凭证"))return "API Key 缺失或不可用";
            if(text.Contains("模型") || text.Contains("model"))return "API 模型不可用";
            return "API 请求失败，请核对连接、模型权限及 Usage。";
        }
        internal static string ValidateUrl(string text)
        {
            Uri url;
            if (!Uri.TryCreate((text??"").Trim(),UriKind.Absolute,out url) || url.Scheme!="https" ||
                String.IsNullOrEmpty(url.Host) || url.UserInfo.Length!=0 || url.Query.Length!=0 || url.Fragment.Length!=0 ||
                text.IndexOfAny(new[]{'\\','"','?','#','@'})>=0)
                throw new ArgumentException("Base URL 需要 HTTPS 地址，不能含账号、查询参数或片段。");
            string host = url.IdnHost.TrimEnd('.');
            if (IsWebsite(host, "platform.openai.com") || IsWebsite(host, "developers.openai.com") ||
                IsWebsite(host, "chatgpt.com") || IsWebsite(host, "help.openai.com") || host == "openai.com" || host == "www.openai.com")
                throw new ArgumentException("该地址是网站。OpenAI 官方 Base URL 请填 " + DefaultUrl + "。");
            if (host == "api.openai.com")
            {
                if (url.IsDefaultPort && (url.AbsolutePath == "/" || url.AbsolutePath.TrimEnd('/') == "/v1"))
                    return DefaultUrl;
                throw new ArgumentException("OpenAI 官方 Base URL 请填 " + DefaultUrl + "，无需追加 /models 或 /responses。");
            }
            return url.AbsoluteUri.TrimEnd('/');
        }

        private static bool IsWebsite(string host, string domain)
        {
            return host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        internal static string Preflight(string url, string model)
        {
            string address = ValidateUrl(url);
            model = (model ?? "").Trim();
            if (model.Length == 0) throw new ArgumentException("请在设置中填写或选择 API 模型 ID。");
            if (!AgentRunner.ValidModel(model))
                throw new ArgumentException("模型名称无效，请填写完整 API 模型 ID。");
            if (address == DefaultUrl && Regex.IsMatch(model, @"^\d+(?:\.\d+)?[- ]?(?:sol|astra|terra|luna)$", RegexOptions.IgnoreCase))
                throw new ArgumentException("请输入完整 API 模型 ID，例如项目已开通的 gpt-5.4-mini。");
            return address;
        }
        internal static void Save(bool api,string url,string model)
        {
            model=(model??"").Trim();
            string address;
            if (api) address = Preflight(url, model);
            else
            {
                try { address = ValidateUrl(url); }
                catch (ArgumentException) { address = DefaultUrl; }
            }
            var next=new CodexConnectionPreferences{Mode=api?"api":"account",BaseUrl=address,Model=model};
            if(api)ResolveApiKey();
            lock (preferenceLock)
            {
                AgentData.Save(FileName,next);
                corrupt = false;
                current = next;
            }
            CodexAppServer.ResetConnection();
        }
        internal static string Provider { get { return "one_click_film_https"; } }
        internal static Dictionary<string,object> Settings()
        {
            return CodexAppServer.Obj("name", "OpenAI", "wire_api", "responses",
                "requires_openai_auth", true, "supports_websockets", false);
        }
        internal static string[] Arguments()
        {
            var json=AgentData.Json();
            string table=String.Join(",",Settings().Select(x=>x.Key+"="+json.Serialize(x.Value)));
            return new[]{"-c","model_provider="+json.Serialize(Provider),"-c","model_providers."+Provider+"={"+table+"}"};
        }
        internal static void Configure(ProcessStartInfo start,bool provider)
        {
            start.EnvironmentVariables.Remove(Variable);
            start.EnvironmentVariables.Remove("LACUNA_OPENAI_API_KEY");
            if (provider && IsApi) throw new InvalidOperationException("Responses API 直连模式不启动 Codex 子进程。");
        }
        internal static CodexApiModels LoadModels(string url)
        {
            return LoadModelsAt(new Uri(ValidateUrl(url)+"/models"),ResolveApiKey());
        }
        internal static string TestKey(string draft, out string source)
        {
            if (!String.IsNullOrEmpty(draft)) { source = "使用当前输入（未保存）"; return AnalysisKeyStore.Normalize(draft); }
            if (HasLaunchKey) { source = "使用本次启动 Key"; return AnalysisKeyStore.Normalize(launchKey); }
            source = "使用已保存的 Key";
            return AnalysisKeyStore.Normalize(AnalysisKeyStore.Read());
        }

        internal static bool ValidRequestId(string id, string key)
        {
            return !String.IsNullOrEmpty(id) && Regex.IsMatch(id, @"\A[A-Za-z0-9_.:/-]{1,128}\z") && RedactTestMessage(id,key) == id;
        }

        private static string SafeRequestId(string id,string key)
        {
            return ValidRequestId(id,key) ? id : "未提供";
        }

        internal static Dictionary<string,object> ReadError(HttpWebResponse response)
        {
            var buffer = new byte[16384];
            int length = 0;
            try
            {
                using (var stream = response.GetResponseStream())
                {
                    int count;
                    while (length < buffer.Length && (count = stream.Read(buffer, length, buffer.Length-length)) > 0)
                        length += count;
                }
            }
            catch (IOException) { } // The HTTP status remains authoritative if its explanation was truncated.
            catch (WebException) { }
            string text = Encoding.UTF8.GetString(buffer,0,length);
            try
            {
                var json = AgentData.Json().Deserialize<Dictionary<string,object>>(text);
                var error = CodexAppServer.Child(json, "error");
                if (error != null) return error;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            if (Regex.IsMatch(text, @"^\s*(?:<!doctype\s+html|<html\b)", RegexOptions.IgnoreCase))
                return CodexAppServer.Obj("message", "服务端返回 HTML 错误页，请检查 Base URL 或网络代理。");
            return CodexAppServer.Obj("message", text);
        }

        private static void CheckModelRedirect(int code)
        {
            if (code >= 300 && code < 400)
                throw new InvalidOperationException("Base URL 发生重定向（HTTP " + code + "），请填写最终 API 地址（OpenAI 官方为 " + DefaultUrl + "）。");
        }

        private static CodexApiModels LoadModelsAt(Uri endpoint,string key)
        {
            return RequestModels(endpoint,key,Stopwatch.StartNew(),CancellationToken.None);
        }

        internal static CodexApiModels RequestModels(Uri endpoint, string key, Stopwatch clock, CancellationToken cancel)
        {
            try { return ReadModels(endpoint,key,clock,cancel); }
            catch(Exception error){
                string safe=RedactTestMessage(error.Message,key);
                if(safe==error.Message)throw;
                throw new InvalidOperationException(safe);
            }
        }

        private static CodexApiModels ReadModels(Uri endpoint,string key,Stopwatch clock,CancellationToken cancel)
        {
            var request=(HttpWebRequest)WebRequest.Create(endpoint);
            request.AllowAutoRedirect=false;
            if (endpoint.IsLoopback) request.Proxy = null;
            request.Timeout=request.ReadWriteTimeout=15000;
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+key;
            using(var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
            Action check = delegate {
                cancel.ThrowIfCancellationRequested();
                if(clock.ElapsedMilliseconds >= 15000 || deadline.IsCancellationRequested) throw new TimeoutException("15 秒内未完成");
            };
            check();
            deadline.CancelAfter(Math.Max(1,15000-(int)clock.ElapsedMilliseconds));
            using(deadline.Token.Register(request.Abort))
            try
            {
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var reader=new StreamReader(response.GetResponseStream()))
                {
                    CheckModelRedirect((int)response.StatusCode);
                    if (!String.IsNullOrEmpty(response.ContentType) && response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("网址返回网页 · 连接目标返回了网页。请检查 Base URL，OpenAI 官方地址为 " + DefaultUrl + "。");
                    var body=new StringBuilder();
                    var buffer=new char[4096];
                    int count;
                    while((count=reader.Read(buffer,0,buffer.Length))>0)
                    {
                        check();
                        if(body.Length+count>2*1024*1024)throw new InvalidDataException("模型列表响应过大。");
                        body.Append(buffer,0,count);
                    }
                    var json=body.ToString();
                    if(Regex.IsMatch(json,@"^\s*(?:<!doctype\s+html|<html\b)",RegexOptions.IgnoreCase))
                        throw new InvalidDataException("网址返回网页 · 连接目标返回了网页，请检查 Base URL。");
                    Dictionary<string,object> data;
                    try {data=AgentData.Json().Deserialize<Dictionary<string,object>>(json);}
                    catch(Exception error)
                    {
                        if(!(error is ArgumentException || error is InvalidOperationException))throw;
                        throw new InvalidDataException("HTTP "+(int)response.StatusCode+"，返回的模型列表格式无效。");
                    }
                    if(data==null || !(AgentData.Value(data,"data") is System.Collections.IList))
                        throw new InvalidDataException("HTTP "+(int)response.StatusCode+"，响应缺少模型列表。");
                    var ids = CodexAppServer.Items(data,"data").Select(item => CodexAppServer.S(item,"id"))
                        .Where(id => AgentRunner.ValidModel(id) && !id.Contains(key) && RedactDiagnostic(id)==id)
                        .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    check();
                    var idHeader = response.Headers["x-request-id"];
                    return new CodexApiModels(ids, "HTTP " + (int)response.StatusCode + " · 请求 " + SafeRequestId(idHeader,key));
                }
            }
            catch(WebException e)
            {
                using(var res=e.Response as HttpWebResponse)
                {
                    check();
                    if(e.Status==WebExceptionStatus.Timeout)throw new TimeoutException("15 秒内未完成");
                    if (res == null) throw new InvalidOperationException("网络连接失败 · " + e.Status);
                    int code = (int)res.StatusCode;
                    CheckModelRedirect(code);
                    string detail = AgentData.Short(RedactTestMessage(AgentData.Str(ReadError(res), "message"),key), 360);
                    check();
                    string hint = code == 401 ? "鉴权失败" : code == 403 ? "缺少 List models / Read 权限" :
                        code == 429 ? "请求受限，请检查额度与速率限制" : code == 404 ? "接口地址不存在" :
                        code == 400 ? "请求参数无效" : code >= 500 ? "服务端错误" : "请求失败";
                    throw new InvalidOperationException(hint + " · HTTP " + code + " · 请求 " + SafeRequestId(res.Headers["x-request-id"],key) +
                        (detail.Length == 0 ? "" : " · " + detail));
                }
            }
            catch(IOException) { check(); throw new IOException("网络连接失败"); }
            }
        }
    }
}
