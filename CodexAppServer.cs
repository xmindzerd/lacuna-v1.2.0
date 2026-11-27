using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace OneClickFilm
{
    // The stdout reader only routes envelopes. Project code and UI callbacks run on the calling worker.
    internal sealed partial class CodexAppServer : IDisposable
    {
        private static readonly object poolLock = new object();
        private static readonly Dictionary<string,CodexAppServer> servers = new Dictionary<string,CodexAppServer>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,DateTime> retryAt = new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,string> openErrors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal static string OpenReason(string exe)
        {
            lock(poolLock){
                string reason;
                return exe!=null && openErrors.TryGetValue(exe,out reason)?reason:"此程序未提供可用的常驻协议";
            }
        }
        private static bool shuttingDown;
        private static readonly HashSet<CodexAppServer> owned=new HashSet<CodexAppServer>();
        private sealed class Envelope {internal int Request; internal string Json; internal CancellationToken Cancel;}
        private readonly BlockingCollection<Envelope> writes=new BlockingCollection<Envelope>();
        private readonly Process process;
        private readonly TaskProcessGroup group;
        private readonly ConcurrentDictionary<int,Request> replies = new ConcurrentDictionary<int,Request>();
        private readonly ConcurrentDictionary<string,BlockingCollection<Map>> threads = new ConcurrentDictionary<string,BlockingCollection<Map>>();
        private int sequence, closed, retired, active;
        private StreamWriter input;
        private StreamReader stdout, stderr;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            internal int cb; internal string Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal short ShowWindow, Reserved2; internal IntPtr Reserved3;
            internal SafeFileHandle StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes sa, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags, IntPtr env, string cwd, ref StartupInfo si, out ProcessInfo pi);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        internal static Map Obj(params object[] fields)
        {
            var data = new Map();
            for (int i=0;i<fields.Length;i+=2) data.Add((string)fields[i],fields[i+1]);
            return data;
        }
        internal static Map Child(Map data,string key) { return AgentData.Value(data,key) as Map; }
        internal static IEnumerable<Map> Items(Map data,string key)
        {
            var values=AgentData.Value(data,key) as System.Collections.IEnumerable;
            return values == null ? Enumerable.Empty<Map>() : values.OfType<Map>();
        }
        internal static string S(Map data,string key) { return AgentData.Str(data,key); }

        internal static CodexAppServer Open(string exe, CancellationToken cancel=default(CancellationToken))
        {
            if (CodexAuthMode.IsApi || String.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) return null;
            lock(poolLock)
            {
                cancel.ThrowIfCancellationRequested();
                if(shuttingDown) return null;
                CodexAppServer server;
                if(servers.TryGetValue(exe,out server) && server.closed==0 && server.retired==0) return server;
                DateTime retry;
                if(retryAt.TryGetValue(exe,out retry) && retry>DateTime.UtcNow) return null;
                server=null;
                try
                {
                    server=new CodexAppServer(exe);
                    server.Call("initialize",new {clientInfo=new {name="lacuna",title="Lacuna",version=Program.Version},
                        capabilities=new {experimentalApi=true}},10000,cancel);
                    server.Write(new {method="initialized"});
                    openErrors.Remove(exe);
                    servers[exe]=server;
                    owned.Add(server);
                    return server;
                }
                catch(Exception e)
                {
                    if(server!=null) server.Dispose();
                    var rejection=e as Rejected;
                    if(rejection!=null && !rejection.Compatibility) throw;
                    if(!(e is IOException || e is Win32Exception || e is InvalidOperationException || e is TimeoutException)) throw;
                    openErrors[exe]=CodexAuthMode.RedactDiagnostic(e.Message);
                    retryAt[exe]=DateTime.UtcNow.AddSeconds(30);
                    return null;
                }
            }
        }

        private CodexAppServer(string exe)
        {
            var args=new List<string>{"app-server","-c","web_search=\"disabled\"","-c","approval_policy=\"never\"",
                "-c","sandbox_mode=\"read-only\"","-c","mcp_servers={}"};
            args.AddRange(CodexAuthMode.Arguments());
            foreach(string f in new[]{"shell_tool","image_generation","hooks","apps","plugins","computer_use","browser_use","multi_agent"})
                args.AddRange(new[]{"--disable",f});
            // Bypass .NET Framework 4.x Process.Start StandardInput which flushes a UTF-8 BOM when ConsoleCP/system locale is 65001.
            var sa=new SecurityAttributes{Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=true};
            SafeFileHandle inR=null,inW=null,outR=null,outW=null,errR=null,errW=null;
            var pi=new ProcessInfo();
            try
            {
                if(!CreatePipe(out inR,out inW,ref sa,0) || !CreatePipe(out outR,out outW,ref sa,0) || !CreatePipe(out errR,out errW,ref sa,0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!SetHandleInformation(inW,1,0) || !SetHandleInformation(outR,1,0) || !SetHandleInformation(errR,1,0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var si=new StartupInfo{cb=Marshal.SizeOf(typeof(StartupInfo)),Flags=0x00000100,StdInput=inR,StdOutput=outW,StdError=errW};
                var cmd=new StringBuilder(AgentRunner.Quote(exe)+" "+AgentRunner.Args(args));
                var environment=new ProcessStartInfo();
                CodexAuthMode.Configure(environment,true);
                var vars=environment.EnvironmentVariables;
                var block=String.Join("\0",vars.Keys.Cast<string>().OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).Select(x=>x+"="+vars[x]))+"\0";
                var memory=Marshal.StringToHGlobalUni(block);
                try
                {
                    if(!CreateProcess(null,cmd,IntPtr.Zero,IntPtr.Zero,true,0x08000404,memory,Environment.SystemDirectory,ref si,out pi))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { Marshal.FreeHGlobal(memory); }
                process=Process.GetProcessById(pi.ProcessId);
                group=TaskProcessGroup.Attach(process);
                if(group==null) { TerminateProcess(pi.Process,1); throw new IOException("无法托管常驻 Codex 子进程。"); }
                if(ResumeThread(pi.Thread)==unchecked((uint)-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                input=new StreamWriter(new FileStream(inW,FileAccess.Write,4096,false),new UTF8Encoding(false)){AutoFlush=true}; inW=null;
                stdout=new StreamReader(new FileStream(outR,FileAccess.Read,4096,false),new UTF8Encoding(false)); outR=null;
                stderr=new StreamReader(new FileStream(errR,FileAccess.Read,4096,false),new UTF8Encoding(false)); errR=null;
                new Thread(DrainError){IsBackground=true,Name="Lacuna Codex stderr"}.Start();
                new Thread(ReadLoop){IsBackground=true,Name="Lacuna Codex stdio"}.Start();
                new Thread(WriteLoop){IsBackground=true,Name="Lacuna Codex stdin"}.Start();
            }
            catch { Dispose(); throw; }
            finally
            {
                if(pi.Thread!=IntPtr.Zero) CloseHandle(pi.Thread);
                if(pi.Process!=IntPtr.Zero) CloseHandle(pi.Process);
                if(inR!=null) inR.Dispose(); if(outW!=null) outW.Dispose(); if(errW!=null) errW.Dispose();
                if(inW!=null) inW.Dispose(); if(outR!=null) outR.Dispose(); if(errR!=null) errR.Dispose();
            }
        }

        private void DrainError()
        {
            try { while(stderr.ReadLine()!=null) { } }
            catch(IOException) { }
            catch(ObjectDisposedException) { }
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while((line=stdout.ReadLine())!=null)
                {
                    Map msg;
                    try { msg=AgentData.Json().Deserialize<Map>(CodexAuthMode.Redact(line)); } catch(ArgumentException){continue;}
                    if(msg==null) continue;
                    if(msg.ContainsKey("id"))
                    {
                        if(msg.ContainsKey("method"))
                        {
                            Write(new {id=msg["id"],error=new {code=-32601,message="Interactive permissions are unavailable."}});
                            continue;
                        }
                        Request pending;
                        int id;
                        if(Int32.TryParse(S(msg,"id"),out id) && replies.TryGetValue(id,out pending))
                        {
                            if(pending.Method=="account/rateLimits/read" && !msg.ContainsKey("error"))
                                UpdateQuota(Child(msg,"result"),true);
                            pending.Reply.TrySetResult(msg);
                        }
                        continue;
                    }
                    var p=Child(msg,"params");
                    if(S(msg,"method")=="account/rateLimits/updated")
                    {
                        var quota=Child(p,"rateLimits");
                        UpdateQuota(quota,false);
                    }
                    BlockingCollection<Map> queue;
                    if(threads.TryGetValue(S(p,"threadId"),out queue)) queue.Add(msg);
                }
            }
            catch(IOException) { }
            catch(InvalidOperationException) { }
            catch(ArgumentException) { }
            finally { Dispose(); }
        }

        private void Write(object msg,int id=0,CancellationToken cancel=default(CancellationToken))
        {
            if(closed!=0)throw new IOException("Codex 常驻连接已断开。");
            try{writes.Add(new Envelope{Request=id,Cancel=cancel,Json=AgentData.Json().Serialize(msg)});}
            catch(InvalidOperationException e){throw new IOException("Codex 常驻连接已断开。",e);}
        }
        private void WriteLoop()
        {
            try
            {
                foreach(var envelope in writes.GetConsumingEnumerable())
                    {
                    Request pending;
                    if(envelope.Request!=0 && !replies.TryGetValue(envelope.Request,out pending)) continue;
                    if(envelope.Cancel.IsCancellationRequested)
                    {
                        if(replies.TryGetValue(envelope.Request,out pending))pending.Reply.TrySetCanceled();
                        continue;
                    }
                    input.WriteLine(envelope.Json);
                }
            }
            catch(IOException){}
            catch(InvalidOperationException){}
            finally {Dispose();try{input.Dispose();}catch(IOException){}catch(ObjectDisposedException){}}
        }
        private sealed class Rejected : IOException
        {
            internal readonly bool Compatibility;
            internal Rejected(Map error,string method) : base(S(error,"message"))
            {
                string code=S(error,"code"),detail=Message.Trim().ToLowerInvariant();
                var data=Child(error,"data");
                if(method=="turn/start" && code=="-32600" && String.IsNullOrEmpty(S(data,"type"))){
                    Compatibility=detail.Contains("one_click_film_https") && detail.Contains("current provider is openai");
                    return;
                }
                if(data!=null)
                {
                    Compatibility=method=="turn/start" && code=="-32600" && S(data,"type")=="modelProviderMismatch" &&
                        S(data,"modelProvider")=="one_click_film_https" && S(data,"currentModelProvider")=="openai";
                    return;
                }
                if(code=="-32601")
                {
                    string name=method.ToLowerInvariant();
                    Compatibility=detail=="method not found" || detail=="method not found: "+name || detail=="unknown method: "+name;
                    return;
                }
                // Invalid params alone may be an explicit model, approval or policy denial.
                Compatibility=method=="turn/start" && code=="-32602" && System.Text.RegularExpressions.Regex.IsMatch(detail,
                    @"^(?:invalid (?:request|params)(?::| parameters:)\s*)?unknown field [`'""](?:outputschema|effort)[`'""](?:, expected [^\r\n]+)?$");
            }
        }
        private sealed class Request
        {
            internal int Id;
            internal string Method;
            internal TaskCompletionSource<Map> Reply=new TaskCompletionSource<Map>();
        }
        private Request Begin(string method,object parameters,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var request=new Request{Id=Interlocked.Increment(ref sequence),Method=method};
            replies[request.Id]=request;
            try { Write(new {id=request.Id,method=method,@params=parameters},request.Id,cancel); }
            catch { Forget(request); throw; }
            return request;
        }
        private void Forget(Request request)
        {
            Request ignored;
            if(request!=null)replies.TryRemove(request.Id,out ignored);
        }
        private static Map Await(Request request,int timeout,CancellationToken cancel)
        {
            var task=request.Reply.Task;
            if(!task.IsCompleted)
            {
                var ready=((IAsyncResult)task).AsyncWaitHandle;
                if(cancel.CanBeCanceled)
                {
                    var signal=WaitHandle.WaitAny(new[]{ready,cancel.WaitHandle},timeout);
                    cancel.ThrowIfCancellationRequested();
                    if(signal==WaitHandle.WaitTimeout)throw new TimeoutException("Codex 接口超时 · "+request.Method);
                }
                else if(!ready.WaitOne(timeout))throw new TimeoutException("Codex 接口超时 · "+request.Method);
            }
            cancel.ThrowIfCancellationRequested();
            Map msg=task.GetAwaiter().GetResult();
            if(msg==null)throw new IOException("Codex 常驻连接已断开。");
            var error=Child(msg,"error");
            if(error!=null)throw new Rejected(error,request.Method);
            return Child(msg,"result") ?? new Map();
        }
        private Map Call(string method,object parameters,int timeout=15000,CancellationToken cancel=default(CancellationToken))
        {
            var request=Begin(method,parameters,cancel);
            try{return Await(request,timeout,cancel);}
            finally{Forget(request);}
        }

        internal static void ResetConnection()
        {
            lock(poolLock)
            {
                foreach(var server in owned.ToArray())server.Dispose();
                servers.Clear();
                retryAt.Clear();
                openErrors.Clear();
                displayed=null;
            }
        }

        internal static void Shutdown()
        {
            lock(poolLock)
            {
                shuttingDown=true;
                foreach(var server in owned.ToArray()) server.Dispose();
                owned.Clear();
                servers.Clear();
            }
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref closed,1)!=0) return;
            writes.CompleteAdding();
            foreach(var p in replies.Values) p.Reply.TrySetResult(null);
            if(group!=null) {group.Stop();group.Dispose();}
            else if(process!=null) CodexConnection.StopProcessTree(process);
            if(process!=null) process.Dispose();
            try{if(stdout!=null)stdout.Dispose();}catch(IOException){}catch(ObjectDisposedException){}
            try{if(stderr!=null)stderr.Dispose();}catch(IOException){}catch(ObjectDisposedException){}
            lock(poolLock)owned.Remove(this);
        }
    }
}
