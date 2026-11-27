using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace OneClickFilm
{
    internal sealed partial class CodexAppServer
    {
        private static readonly ConcurrentDictionary<string,byte> Resuming=new ConcurrentDictionary<string,byte>();
        private static readonly string[] BasicTools={"start_menu","get_project","read_project_script","read_project_memory","list_locations","show_in_app"};
        

        private Map Start(string work,string model,string effort,string session,bool fork,bool ephemeral,IList<string> bridge,string binding,CancellationToken cancel,out HashSet<string> allowed,ref Request opening)
        {
            // The CLI merges tables from user / project layers even when mcp_servers={} was passed.
            var cfg=Child(Call("config/read",new {cwd=work},15000,cancel),"config");
            var expected=CodexAuthMode.Settings();
            var actual=Child(Child(cfg,"model_providers"),CodexAuthMode.Provider);
            if(cfg==null || (CodexAuthMode.IsApi && actual==null) ||
                (actual!=null && (S(cfg,"model_provider")!=CodexAuthMode.Provider ||
                 expected.Any(p=>!Object.Equals(AgentData.Value(actual,p.Key),p.Value)) ||
                 actual.Any(p=>!expected.ContainsKey(p.Key) && p.Value!=null && !(p.Key=="supports_standalone_web_search" && Object.Equals(p.Value,false))))))
                throw new IOException("当前配置无法隔离 HTTPS provider。");
            var inherited=Child(cfg,"mcp_servers") ?? new Map();
            var mcp=new Map();
            foreach(var name in inherited.Keys) mcp[name]=new {enabled=false};
            bool headless = ephemeral && (bridge.Count <= 1 || String.IsNullOrEmpty(bridge[1])) && String.IsNullOrEmpty(session);
            allowed=new HashSet<string>(headless ? new string[0] : BasicTools,StringComparer.Ordinal);
            if(bridge.Count>4 && !String.IsNullOrEmpty(bridge[4])) allowed.Add("read_selected_skill_file");
            if(inherited.ContainsKey("oneclick_film")) throw new IOException("用户已注册同名 MCP，改用独立配置。");
            var arguments=new List<string>(bridge);
            if(arguments.Count==4)arguments.Add("");
            arguments.Add(binding);
            mcp["oneclick_film"]=new {command=Assembly.GetExecutingAssembly().Location,args=arguments,required=true,
                enabled_tools=allowed.ToArray(),tools=new {show_in_app=new {approval_mode="approve"}}};
            var settings=Obj("mcp_servers",mcp,"model_providers",Obj(CodexAuthMode.Provider,expected),
                "web_search","disabled","features",Obj("shell_tool",false,"image_generation",false,"hooks",false,"apps",false,"plugins",false,
                    "browser_use",false,"computer_use",false,"multi_agent",false));
            if(!String.IsNullOrEmpty(effort)) settings["model_reasoning_effort"]=effort;
            var options=Obj("cwd",work,"modelProvider",CodexAuthMode.Provider,"approvalPolicy","never","sandbox","read-only","config",settings);
            if(!String.IsNullOrWhiteSpace(model)) options["model"]=model;
            string method="thread/start";
            if(!String.IsNullOrEmpty(session))
            {
                method=fork?"thread/fork":"thread/resume";
                options["threadId"]=session;
            }
            if(method!="thread/resume") options["ephemeral"]=ephemeral;
            opening=Begin(method,options,cancel);
            return Await(opening,30000,cancel);
        }

        private void Verify(string thread,Map started,HashSet<string> allowed,string binding,string work,CancellationToken cancel)
        {
            var sandbox=Child(started,"sandbox");
            // An explicitly requested legacy sandbox normally has null profile provenance.
            // A custom profile, even one extending read-only, can change its effective permissions.
            var profile=Child(started,"activePermissionProfile");
            if(AgentData.Value(started,"activePermissionProfile")!=null &&
                (profile==null || S(profile,"id")!=":read-only" || S(profile,"extends").Length!=0))
                throw new UnauthorizedAccessException("Codex 返回了本轮无法验证的权限配置。");
            if(!String.Equals(S(started,"cwd").Replace('/','\\').TrimEnd('\\','/'),Path.GetFullPath(work).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
                throw new IOException("Codex 返回的工作目录与任务不符。");
            if(S(started,"approvalPolicy").Length==0 || sandbox==null || AgentData.Value(sandbox,"networkAccess")==null)
                throw new IOException("当前 CLI 无法报告有效沙箱配置。");
            if(S(started,"approvalPolicy")!="never" || S(sandbox,"type")!="readOnly" || !Object.Equals(AgentData.Value(sandbox,"networkAccess"),false))
                throw new UnauthorizedAccessException("Codex 返回的有效权限与本轮审批或只读沙箱不符。");
            if(S(started,"modelProvider")!=CodexAuthMode.Provider)throw new IOException("Codex 返回的 provider 与任务不符。");
            var result=Call("mcpServerStatus/list",new {threadId=thread,limit=200},20000,cancel);
            bool found=false;
            foreach(var s in Items(result,"data"))
            {
                var tools=Child(s,"tools") ?? new Map();
                if(S(s,"name")=="oneclick_film") found=allowed.SetEquals(tools.Keys) && S(Child(s,"serverInfo"),"version")==Program.Version+"+"+binding;
                else if(tools.Count>0 || Items(s,"resources").Any() || Items(s,"resourceTemplates").Any())
                    throw new IOException("Codex 线程包含本轮未选择的 MCP。");
            }
            if(!found || !String.IsNullOrEmpty(S(result,"nextCursor"))) throw new IOException("无法确认本轮 MCP 白名单。");
        }

        internal AgentResult TryTurn(string work,string text,string model,string effort,string session,bool fork,string schema,string output,
            IList<string> images,IList<string> bridge,Action<AgentEvent> notify,CancellationToken cancel,int timeout,int idle,Action<string> diagnostic,Action<string> sent)
        {
            string thread=null,turn=null;
            bool terminal=false,owns=false,rejected=false,finalAnswer=false;
            Request opening=null,admission=null;
            var queue=new BlockingCollection<Map>();
            var result=new AgentResult{TransportNote="Codex app-server"};
            Action<string,string> emit=(type,value)=>{if(notify!=null)notify(new AgentEvent{Type=type,Text=value});};
            bool resume=!String.IsNullOrEmpty(session) && !fork;
            if(resume && !Resuming.TryAdd(session,0))throw new InvalidOperationException("同一 Codex 会话已有轮次运行，请待本轮结束后继续。");
            Interlocked.Increment(ref active);
            try
            {
                cancel.ThrowIfCancellationRequested();
                if(retired!=0) return null;
                HashSet<string> allowed;
                string binding=Guid.NewGuid().ToString("N");
                var started=Start(work,model,effort,session,fork,schema!=null,bridge,binding,cancel,out allowed,ref opening);
                thread=S(Child(started,"thread"),"id");
                if(thread.Length==0 || !threads.TryAdd(thread,queue)) throw new InvalidOperationException("无法独占 Codex 会话。");
                owns=true;
                Verify(thread,started,allowed,binding,work,cancel);
                if (CodexAuthMode.IsApi && notify != null)
                    notify(new AgentEvent{Type="audit_model", ReportedModel=S(started,"model")});
                var inputs=new List<object>{new {type="text",text=text}};
                if(images!=null) inputs.AddRange(images.Select(path=>(object)new {type="localImage",path=Path.GetFullPath(path)}));
                var options=Obj("threadId",thread,"input",inputs);
                if(schema!=null) options["outputSchema"]=AgentData.Json().DeserializeObject(File.ReadAllText(schema));
                if(!String.IsNullOrEmpty(effort)) options["effort"]=effort;
                cancel.ThrowIfCancellationRequested();
                if(FlowCreation.Development)DevMonitor.Log(null,"CODEX-EV","通过 app-server 预检 · thread "+thread);
                if(sent!=null)sent("app-server");
                admission=Begin("turn/start",options,cancel);
                turn=S(Child(Await(admission,30000,cancel),"turn"),"id");
                if(String.IsNullOrEmpty(turn)) throw new IOException("Codex 未返回轮次编号。");
                if(FlowCreation.Development)DevMonitor.Log(null,"CODEX-EV","已接单 · turn "+turn);
                result.Session=thread;
                emit("transport","app-server"); emit("session",thread); emit("started",null);
                var elapsed=Stopwatch.StartNew();
                long last=0, painted=-500;
                string tail="";
                while(!terminal)
                {
                    cancel.ThrowIfCancellationRequested();
                    if(closed!=0 && queue.Count==0) throw new IOException("Codex 常驻进程已断开，本轮状态待核对。");
                    if(elapsed.ElapsedMilliseconds>timeout || idle>0 && elapsed.ElapsedMilliseconds-last>idle)
                        throw new TimeoutException("Codex 常驻轮次等待超时。");
                    Map msg;
                    if(!queue.TryTake(out msg,120)) continue;
                    Map p=Child(msg,"params");
                    string method=S(msg,"method");
                    string tid=S(p,"turnId");
                    if(tid.Length!=0 && tid!=turn) continue;
                    var completed=Child(p,"turn");
                    if(completed!=null && S(completed,"id")!=turn) continue;
                    last=elapsed.ElapsedMilliseconds;
                    var item=Child(p,"item");
                    switch(method)
                    {
                        case "item/agentMessage/delta":
                            if(finalAnswer)break;
                            tail+=S(p,"delta");
                            if(tail.Length>128)tail=tail.Substring(tail.Length-128);
                            if(last-painted>=300){emit("delta",tail);painted=last;}
                            break;
                        case "item/reasoning/summaryTextDelta": case "item/reasoning/textDelta":
                            if(last-painted>=1500){emit("reasoning",null);painted=last;} break;
                        case "item/started":
                            if(S(item,"type")=="reasoning")emit("reasoning",null);
                            if(S(item,"type")=="mcpToolCall"){result.Calls++;emit("step",S(item,"tool"));}
                            break;
                        case "item/completed":
                            if(S(item,"type")=="agentMessage")
                            {
                                string phase=S(item,"phase"),body=S(item,"text");
                                if(phase=="final_answer" || (!finalAnswer && phase.Length==0))
                                {
                                    result.Answer=body;
                                    finalAnswer=phase=="final_answer";
                                    emit("message",body);
                                }
                                else if(!finalAnswer && body.Length!=0)emit("delta",body);
                                tail="";
                            }
                            if(FlowCreation.Development && S(item,"type")=="mcpToolCall")DevMonitor.Log(null,"CODEX-EV","工具 "+S(item,"tool")+" · "+S(item,"status"));
                            if(S(item,"type")=="mcpToolCall" && S(item,"status")=="failed") emit("tool_error",S(item,"tool"));
                            break;
                        case "model/rerouted":
                            if(CodexAuthMode.IsApi && notify!=null)
                                notify(new AgentEvent{Type="audit_model",ReportedModel=S(p,"toModel")});
                            break;
                        case "thread/tokenUsage/updated":
                            var usage=Child(Child(p,"tokenUsage"),"last");
                            result.HasUsage=usage!=null && usage.ContainsKey("inputTokens") && usage.ContainsKey("outputTokens");
                            result.Input=AgentData.Number(usage,"inputTokens");result.Cached=AgentData.Number(usage,"cachedInputTokens");
                            result.Output=AgentData.Number(usage,"outputTokens");
                            if(CodexAuthMode.IsApi && notify!=null && usage!=null)
                                notify(new AgentEvent{Type="audit_usage",HasUsage=true,Input=result.Input,
                                    Output=result.Output,Cached=result.Cached});
                            break;
                        case "turn/completed":
                            if(CodexAuthMode.IsApi && notify!=null)
                                notify(new AgentEvent{Type="audit_request",RequestId=S(completed,"requestId")});
                            terminal=true;
                            if(S(completed,"status")!="completed")
                            {
                                var error=Child(completed,"error");
                                var detail=S(error,"message");
                                if(CodexAuthMode.IsApi)
                                    detail=CodexAuthMode.SafeFailure(detail+" "+AgentData.Json().Serialize(AgentData.Value(error,"codexErrorInfo")));
                                throw new IOException("Codex 本轮"+S(completed,"status")+"。 "+detail);
                            }
                            break;
                    }
                }
                if(schema==null && String.IsNullOrWhiteSpace(result.Answer))throw new IOException("Codex 未返回最终正文。");
                if(schema!=null)
                {
                    var data=AgentData.ParseOutput<Map>(result.Answer);
                    AgentData.Save(output,data);
                    result.StructuredText=result.Answer;
                }
                if(notify!=null)
                {
                    var location=Path.Combine(work,AgentMcp.ChoiceFile);
                    if(File.Exists(location))
                    {
                        var choice=AgentData.Read<Map>(location);
                        File.Delete(location);
                        emit("navigate",S(choice,"location"));
                    }
                    notify(new AgentEvent{Type="complete",Input=result.Input,Output=result.Output,Cached=result.Cached});
                }
                return result;
            }
            catch(OperationCanceledException){throw;}
            catch(InvalidDataException){throw;}
            catch(Rejected e)
            {
                if(admission!=null)
                {
                    var observed=queue.ToArray().FirstOrDefault(m=>S(m,"method")=="turn/started" ||
                        S(m,"method")=="turn/completed" || (S(m,"method").StartsWith("item/",StringComparison.Ordinal) && S(Child(m,"params"),"turnId").Length!=0));
                    if(observed!=null)
                    {
                        var details=Child(observed,"params");
                        if(!resume)
                        {
                            turn=S(Child(details,"turn"),"id");
                            if(turn.Length==0)turn=S(details,"turnId");
                            terminal=S(observed,"method")=="turn/completed";
                        }
                        else Interlocked.Exchange(ref retired,1);
                        throw new InvalidOperationException("Codex 已报告轮次活动且随后拒绝请求，执行状态待核对。",e);
                    }
                }
                rejected=true;
                cancel.ThrowIfCancellationRequested();
                if(!e.Compatibility)throw new InvalidOperationException("Codex 拒绝请求。 "+AgentData.Short(e.Message,400),e);
                if(diagnostic!=null)diagnostic("当前 CLI 不兼容本次请求，使用相同配置的 exec · "+CodexAuthMode.RedactDiagnostic(e.Message));
                return null;
            }
            catch(Exception e)
            {
                cancel.ThrowIfCancellationRequested();
                if(admission!=null)
                {
                    string status=terminal?"Codex 本轮已结束。 ":turn==null?"Codex 已发送本轮，受理状态待核对。 ":"Codex 已受理本轮，执行状态待核对。 ";
                    throw new InvalidOperationException(status+AgentData.Short(e.Message,400),e);
                }
                if(!(e is IOException || e is TimeoutException))throw;
                if(diagnostic!=null)diagnostic("Codex app-server 预检未通过，改用 exec · "+AgentData.Short(e.Message,150));
                return null;
            }
            finally
            {
                Action release=delegate
                {
                    try
                    {
                        // If the caller stopped waiting, the correlated reply still belongs to this request.
                        if(opening!=null && thread==null && closed==0)
                        {
                            thread=S(Child(Await(opening,30000,CancellationToken.None),"thread"),"id");
                            owns=thread.Length!=0 && threads.TryAdd(thread,queue);
                        }
                        if(owns && closed==0)
                        {
                            if(admission!=null && !rejected && !terminal)
                            {
                                if(String.IsNullOrEmpty(turn))
                                    turn=ResolveAdmission(admission,queue,!resume,out terminal);
                                if(!terminal)Interrupt(thread,turn,queue);
                            }
                        }
                    }
                    catch(Rejected){} // A rejected opening/admission has no live turn.
                    catch(OperationCanceledException){} // Dropped by the writer before transmission.
                    catch(IOException){Interlocked.Exchange(ref retired,1);}
                    catch(TimeoutException){Interlocked.Exchange(ref retired,1);}
                    finally
                    {
                        if(owns)
                        {
                            try{if(closed==0)Call("thread/unsubscribe",new {threadId=thread},2500);}
                            catch(IOException){} catch(TimeoutException){Interlocked.Exchange(ref retired,1);}
                            BlockingCollection<Map> ignored;threads.TryRemove(thread,out ignored);
                        }
                        Forget(opening);Forget(admission);
                        if(resume){byte reservation;Resuming.TryRemove(session,out reservation);}
                        if(Interlocked.Decrement(ref active)==0 && retired!=0)Dispose();
                    }
                };
                if(cancel.IsCancellationRequested || (opening!=null && !opening.Reply.Task.IsCompleted) ||
                    (admission!=null && !admission.Reply.Task.IsCompleted))
                    ThreadPool.QueueUserWorkItem(delegate{release();});
                else release();
            }
        }

        private string ResolveAdmission(Request request,BlockingCollection<Map> queue,bool newThread,out bool completed)
        {
            completed=false;
            var clock=Stopwatch.StartNew();
            while(newThread && !request.Reply.Task.IsCompleted && clock.ElapsedMilliseconds<30000)
            {
                Map msg;
                if(!queue.TryTake(out msg,100))continue;
                string method=S(msg,"method");
                // Only a freshly allocated thread has no prior or external turn to confuse with ours.
                if(method!="turn/started" && method!="turn/completed")continue;
                string id=S(Child(Child(msg,"params"),"turn"),"id");
                if(id.Length==0)continue;
                completed=method=="turn/completed";
                return id;
            }
            var reply=Await(request,Math.Max(1,30000-(int)clock.ElapsedMilliseconds),CancellationToken.None);
            string turn=S(Child(reply,"turn"),"id");
            if(turn.Length==0)throw new IOException("未收到轮次编号。");
            return turn;
        }

        private void Interrupt(string thread,string turn,BlockingCollection<Map> queue)
        {
            Map msg;
            while(queue.TryTake(out msg))
                if(Completed(msg,turn))return;
            if(FlowCreation.Development)DevMonitor.Log(null,"CODEX-EV","中断轮次 "+turn);
            try {Call("turn/interrupt",new {threadId=thread,turnId=turn},2500);}
            catch(Rejected){} // Completion and the interrupt can cross on the wire.
            var clock=Stopwatch.StartNew();
            while(clock.ElapsedMilliseconds<2500 && closed==0)
                if(queue.TryTake(out msg,100) && Completed(msg,turn))return;
            Interlocked.Exchange(ref retired,1);
        }
        private static bool Completed(Map msg,string turn)
        {
            return S(msg,"method")=="turn/completed" && S(Child(Child(msg,"params"),"turn"),"id")==turn;
        }
    }
}
