using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OneClickFilm
{
    internal enum CodexState { Missing, Unavailable, SignedOut, SignedIn }

    internal sealed class CodexStatus
    {
        internal readonly CodexState State;
        internal readonly string Path;
        internal readonly string Version;
        internal readonly string Detail;

        internal CodexStatus(CodexState state, string path, string version, string detail)
        {
            State = state;
            Path = path;
            Version = version;
            Detail = detail;
        }

        internal string Label
        {
            get
            {
                switch (State)
                {
                    case CodexState.SignedIn: return CodexAuthMode.IsApi?"已配置":"已登录";
                    case CodexState.SignedOut: return CodexAuthMode.IsApi ? (CodexAuthMode.HasKey ? "缺少模型" : "缺少 Key") : "未登录";
                    case CodexState.Missing: return "未找到";
                    default: return "检测失败";
                }
            }
        }
    }

    internal static class CodexConnection
    {
        internal static CodexStatus Check(string selectedPath)
        {
            if (CodexAuthMode.PreferenceCorrupt) return new CodexStatus(CodexState.Unavailable, null, null, CodexAuthMode.PreferenceWarning);
            if (CodexAuthMode.IsApi)
            {
                if (!CodexAuthMode.HasKey)
                    return new CodexStatus(CodexState.SignedOut, null, "Responses API", "请在设置中保存项目 Key。");
                if (String.IsNullOrWhiteSpace(CodexAuthMode.Model))
                    return new CodexStatus(CodexState.SignedOut, null, "Responses API", "请在设置中填写或选择 API 模型 ID。");
                return new CodexStatus(CodexState.SignedIn, null, "Responses API", "OpenAI Responses API 直连已配置");
            }
            if (!String.IsNullOrEmpty(selectedPath))
                return Probe(selectedPath);

            CodexStatus failure = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in Candidates())
            {
                if (String.IsNullOrEmpty(candidate) || !seen.Add(candidate) || !File.Exists(candidate))
                    continue;
                CodexStatus result = Probe(candidate);
                if (result.State == CodexState.SignedIn || result.State == CodexState.SignedOut)
                    return result;
                if (failure == null)
                    failure = result;
            }
            return failure ?? new CodexStatus(CodexState.Missing, null, null,
                "请先安装 Codex，或手动选择已有的 Codex 程序。");
        }

        private static IEnumerable<string> Candidates()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appBin = System.IO.Path.Combine(local, @"OpenAI\Codex\bin");
            foreach (string file in DesktopBinaries(appBin))
                yield return file;

            yield return System.IO.Path.Combine(local, @"Programs\OpenAI\Codex\bin\codex.exe");
            var pathSettings = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
            };
            foreach (string settings in pathSettings)
            {
                foreach (string entry in (settings ?? "").Split(';'))
                {
                    string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                    if (!IsAbsolute(directory))
                        continue;
                    yield return System.IO.Path.Combine(directory, "codex.exe");
                    yield return System.IO.Path.Combine(directory, "codex.cmd");
                }
            }

            yield return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\codex.cmd");
            yield return StoreBinary();
        }

        private static IEnumerable<string> DesktopBinaries(string directory)
        {
            try
            {
                return Directory.GetDirectories(directory).Concat(new[] { directory })
                    .Select(path => new FileInfo(System.IO.Path.Combine(path, "codex.exe")))
                    .Where(file => file.Exists).OrderByDescending(file => file.LastWriteTimeUtc)
                    .Select(file => file.FullName).ToArray();
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static bool IsAbsolute(string path)
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal) ||
                (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }

        private static string StoreBinary()
        {
            // Explorer may start the app without the Codex desktop client's PATH.
            string powershell = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            try
            {
                var package = Run(powershell,
                    "-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name 'OpenAI.Codex' | Sort-Object Version -Descending | Select-Object -First 1).InstallLocation\"",
                    5000);
                string location = package.Output.Trim();
                return package.Code == 0 && IsAbsolute(location)
                    ? System.IO.Path.Combine(location, @"app\resources\codex.exe")
                    : null;
            }
            catch (Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (TimeoutException) { return null; }
        }

        private static CodexStatus Probe(string path)
        {
            string version = null;
            if (!File.Exists(path))
                return new CodexStatus(CodexState.Missing, path, null,
                    "找不到所选的 Codex 程序。请选择新位置或恢复自动查找。");

            try
            {
                var versionResult = Run(path, "--version", 5000);
                string line = versionResult.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(value => value.StartsWith("codex-cli ", StringComparison.OrdinalIgnoreCase));
                if (versionResult.Code != 0 || line == null)
                    return new CodexStatus(CodexState.Unavailable, path, null,
                        "无法读取 Codex 版本。请确认所选程序是可运行的 Codex CLI。");

                version = line.Substring("codex-cli ".Length).Trim();
                var login = Run(path, "login status", 15000);
                if (login.Code == 0)
                {
                    if ((login.Output+login.Error).IndexOf("API key",StringComparison.OrdinalIgnoreCase)>=0)
                        return new CodexStatus(CodexState.SignedOut,path,version,"本机 CLI 当前使用 API Key。请选择 API Key 模式或使用 ChatGPT 账号登录。");
                    string detail = (login.Output + login.Error).IndexOf("using ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "已使用 ChatGPT 登录。"
                        : "本机 Codex 报告登录有效。";
                    return new CodexStatus(CodexState.SignedIn, path, version, detail);
                }
                if (login.Code == 1 && (login.Output + login.Error).IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new CodexStatus(CodexState.SignedOut, path, version,
                        "请在 Codex 登录，再返回重新检测。");
                return new CodexStatus(CodexState.Unavailable, path, version,
                    "Codex 未能读出登录状态。请打开 Codex 检查后重试。");
            }
            catch (TimeoutException)
            {
                return new CodexStatus(CodexState.Unavailable, path, version,
                    "检测超时。请重新检测，或手动选择 Codex 程序。");
            }
            catch (Win32Exception)
            {
                return new CodexStatus(CodexState.Unavailable, path, version,
                    "Windows 无法启动此程序。请检查安装位置和访问权限。");
            }
            catch (InvalidOperationException)
            {
                return new CodexStatus(CodexState.Unavailable, path, version,
                    "Codex 启动后未返回可读状态，请重新检测。");
            }
        }

        internal sealed class CommandResult
        {
            internal int Code;
            internal string Output;
            internal string Error;
            internal bool OutputRecovered;
        }

        internal static IEnumerable<string> SafePathEntries()
        {
            char[] invalid = System.IO.Path.GetInvalidPathChars();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
            };
            foreach (string settings in sources)
            {
                foreach (string raw in (settings ?? "").Split(System.IO.Path.PathSeparator))
                {
                    string entry = Environment.ExpandEnvironmentVariables((raw ?? "").Trim().Trim('"'));
                    if (entry.Length == 0 || entry.IndexOfAny(invalid) >= 0) continue;
                    bool rooted;
                    try { rooted = System.IO.Path.IsPathRooted(entry); }
                    catch (ArgumentException) { continue; }
                    if (rooted && seen.Add(entry)) yield return entry;
                }
            }
        }

        internal static string FindOnPath(string fileName)
        {
            foreach (string dir in SafePathEntries())
            {
                try
                {
                    string candidate = System.IO.Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private static IEnumerable<string> NodeCandidates()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"tools\node");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin");
            string program=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] packages;
            try {packages=Directory.GetDirectories(Path.Combine(program,"WindowsApps"),"OpenAI.CodexPrimaryRuntime*");}
            catch(IOException){packages=new string[0];}catch(UnauthorizedAccessException){packages=new string[0];}
            foreach(string package in packages.OrderByDescending(p=>p))yield return Path.Combine(package,@"dependencies\node\bin");
            yield return Path.Combine(program,"nodejs");
            foreach(string dir in SafePathEntries())yield return dir;
        }
        internal static string NodeDirectory()
        {
            foreach(string dir in NodeCandidates().Distinct(StringComparer.OrdinalIgnoreCase)){
                string path=Path.Combine(dir,"node.exe");
                try {
                    if(!File.Exists(path))continue;
                    int major=FileVersionInfo.GetVersionInfo(path).FileMajorPart;
                    if(major==0){
                        var probe=Run(path,"-p process.versions.node",2000);
                        Version version;
                        if(probe.Code!=0 || !Version.TryParse(probe.Output.Trim().Split('-')[0],out version))continue;
                        major=version.Major;
                    }
                    if(major>=18)return dir;
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(TimeoutException){}catch(System.ComponentModel.Win32Exception){}
            }
            return null;
        }

        internal static CommandResult Run(string file, string arguments, int timeout,
            string workingDirectory = null, string input = null, Action<string> onLine = null,
            System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken), Func<bool> resultReady = null,
            Action<string> onDiagnostic = null, bool useSystemProxy = false, bool codexProvider = false)
        {
            bool script = file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = script ? System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe") : file,
                Arguments = script ? "/d /s /v:off /c \"\"%ONE_CLICK_FILM_CODEX%\" " + arguments + "\"" : arguments,
                WorkingDirectory = (!String.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)) ? workingDirectory : Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input != null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            CodexAuthMode.Configure(start,codexProvider);
            bool novel=String.Equals(System.IO.Path.GetFileName(file),"jimeng-novel.exe",StringComparison.OrdinalIgnoreCase);
            if(novel){
                var nodeDir=NodeDirectory();
                if(nodeDir!=null)start.EnvironmentVariables["PATH"]=nodeDir+";"+start.EnvironmentVariables["PATH"];
            }
            if (useSystemProxy && String.IsNullOrEmpty(start.EnvironmentVariables["HTTPS_PROXY"]))
            {
                try
                {
                    var endpoint = new Uri(novel?"https://jimeng.jianying.com/":"https://dreamina.jianying.com/");
                    var sysProxy = System.Net.WebRequest.GetSystemWebProxy();
                    var proxy = sysProxy == null ? null : sysProxy.GetProxy(endpoint);
                    if (proxy != null && proxy != endpoint && (proxy.Scheme == "http" || proxy.Scheme == "https"))
                    {
                        start.EnvironmentVariables["HTTPS_PROXY"] = proxy.AbsoluteUri;
                        start.EnvironmentVariables["HTTP_PROXY"] = proxy.AbsoluteUri;
                    }
                }
                catch { }
            }
            if (script)
                start.EnvironmentVariables["ONE_CLICK_FILM_CODEX"] = file;
            using (Process child = Process.Start(start))
            {
                if (child == null)
                    throw new InvalidOperationException();
                using (var group = TaskProcessGroup.Attach(child))
                {
                    var elapsed = Stopwatch.StartNew();
                    Task<string> output = onLine == null ? child.StandardOutput.ReadToEndAsync() :
                        ReadLines(child.StandardOutput, onLine);
                    Task<string> error = onDiagnostic == null ? child.StandardError.ReadToEndAsync() :
                        ReadLines(child.StandardError, onDiagnostic, true);
                    var transfers = new List<Task> { output, error };
                    if (input != null)
                        transfers.Add(WriteInput(child.StandardInput.BaseStream, input));
                    var io = Task.WhenAll(transfers);
                    // Interrupted pipes may finish asynchronously after the owned process is stopped.
                    io.ContinueWith(t => { var errorInfo = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    long nextCheck = 0;
                    long exitedAt = -1;
                    while (true)
                    {
                        bool exited = child.WaitForExit(125);
                        if (exited && io.IsCompleted) break;
                        if (exited)
                        {
                            if (exitedAt < 0) exitedAt = elapsed.ElapsedMilliseconds;
                            else if (elapsed.ElapsedMilliseconds - exitedAt >= 1500)
                            {
                                // Release orphaned grandchild handles holding stdout/stderr open after child exited.
                                if (group == null || !group.Stop()) StopProcessTree(child);
                                io.Wait(1000);
                                break;
                            }
                        }
                        if (output.IsFaulted || error.IsFaulted)
                        {
                            if (group == null || !group.Stop()) StopProcessTree(child);
                            io.GetAwaiter().GetResult();
                        }
                        if (cancellation.IsCancellationRequested || elapsed.ElapsedMilliseconds >= timeout)
                        {
                            if (group == null || !group.Stop()) StopProcessTree(child);
                            cancellation.ThrowIfCancellationRequested();
                            throw new TimeoutException("等待执行程序超时。");
                        }
                        if (resultReady != null && elapsed.ElapsedMilliseconds >= nextCheck)
                        {
                            nextCheck = elapsed.ElapsedMilliseconds + 1000;
                            bool ready;
                            try { ready = resultReady(); }
                            catch { if (group == null || !group.Stop()) StopProcessTree(child); throw; }
                            if (ready)
                            {
                                if (group == null || !group.Stop()) StopProcessTree(child);
                                return new CommandResult { Code = 0, Output = "", Error = "", OutputRecovered = true };
                            }
                        }
                        if (exited) cancellation.WaitHandle.WaitOne(100);
                    }
                    string outText = output.IsCompleted && !output.IsFaulted && !output.IsCanceled ? output.Result : "";
                    string errText = error.IsCompleted && !error.IsFaulted && !error.IsCanceled ? error.Result : "";
                    if (output.IsFaulted || error.IsFaulted) io.GetAwaiter().GetResult();
                    return new CommandResult { Code = child.ExitCode, Output = CodexAuthMode.Redact(outText), Error = CodexAuthMode.Redact(errText) };
                }
            }
        }

        internal static void StopProcessTree(Process child)
        {
            try
            {
                using (Process kill = Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + child.Id + " /T /F")
                { UseShellExecute = false, CreateNoWindow = true }))
                    if (kill != null) kill.WaitForExit(3000);
                if (!child.HasExited) child.Kill();
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        private static async Task<string> ReadLines(StreamReader reader, Action<string> onLine, bool keepTail = false)
        {
            string line;
            var tail = keepTail ? new StringBuilder() : null;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                line=CodexAuthMode.Redact(line);
                onLine(line);
                if (tail == null) continue;
                tail.AppendLine(line.Length > 1000 ? line.Substring(0, 1000) : line);
                if (tail.Length > 8000) tail.Remove(0, tail.Length - 6000);
            }
            return tail == null ? "" : tail.ToString();
        }

        private static async Task WriteInput(Stream pipe, string input)
        {
            using (pipe)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                await pipe.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
        }
    }
}
