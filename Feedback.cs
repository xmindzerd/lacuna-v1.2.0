using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace OneClickFilm
{
    public sealed class FeedbackEvidence
    {
        public string Id, Source, Text, Image, Time;
    }
    public sealed class FeedbackFinding
    {
        public string[] steps, facts, causes, questions;
        public string stage, expected, actual;
    }
    public sealed class FeedbackCase
    {
        public string Id, Created, Occurred, Project, Stage, Run, Description, Status, Codex, Error;
        public List<FeedbackEvidence> Evidence = new List<FeedbackEvidence>();
        public FeedbackFinding Finding;
    }
    internal static class Feedback
    {
        internal static string Root {get {return AgentData.Inside(AgentData.Local,Path.Combine("问题反馈",System.Security.Principal.WindowsIdentity.GetCurrent().User.Value));}}
        internal static string Folder(FeedbackCase c)
        {
            if(!AgentData.ValidId(c.Id)) throw new InvalidDataException("反馈编号无效");
            return AgentData.Inside(Root,c.Id);
        }
        internal static string Local(FeedbackCase c,string name){return AgentData.Inside(Folder(c),name);}
        internal static string Zip(FeedbackCase c){return Local(c,@"报告\Lacuna-反馈-"+c.Id+".zip");}
        internal static void Save(FeedbackCase c){AgentData.Save(Local(c,"记录.json"),c);}
        internal static IEnumerable<FeedbackCase> List()
        {
            Directory.CreateDirectory(Root);
            foreach(string dir in Directory.GetDirectories(Root).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                string id=Path.GetFileName(dir);
                if(!AgentData.ValidId(id))continue;
                var c=AgentData.Read<FeedbackCase>(AgentData.Inside(Root,Path.Combine(id,"记录.json")));
                if(c!=null && c.Id==id)yield return c;
            }
        }
        internal static string Scrub(string text)
        {
            string s=text??"";
            s=Regex.Replace(s,@"(?i)(?:https?|wss?)://[^\s""<>]+","[链接]");
            s=Regex.Replace(s,@"(?i)\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_.=:-]+","[凭据]");
            s=Regex.Replace(s,@"(?i)[""']?(?:[\w-]*(?:token|password|secret|cookie|authorization|api[_-]?key|signature)|x-amz-credential|sig)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n,;}]+)","[凭据]");
            s=Regex.Replace(s,@"(?i)\b(?:sk-|sess-|eyJ)[A-Za-z0-9_.-]{8,}","[凭据]");
            s=Regex.Replace(s,@"(?i)(?:\b[A-Z]:[\\/]|\\\\[^\\\s]+\\|/(?:Users|home)/)[^\r\n""<>|]*","[本机路径]");
            return s;
        }
        private static IEnumerable<string> Files(string root,string sub,string pattern,int depth)
        {
            string start=AgentData.Inside(root,sub);
            if(!Directory.Exists(start))yield break;
            var todo=new Queue<Tuple<string,int>>();
            todo.Enqueue(Tuple.Create(start,depth));
            while(todo.Count>0)
            {
                var next=todo.Dequeue();
                foreach(var file in Directory.EnumerateFiles(next.Item1,pattern))
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0)yield return file;
                if(next.Item2>0)foreach(var d in Directory.EnumerateDirectories(next.Item1))
                    if((File.GetAttributes(d)&FileAttributes.ReparsePoint)==0)todo.Enqueue(Tuple.Create(d,next.Item2-1));
            }
        }
        private static T ReadSummary<T>(string project, string relative) where T : class
        {
            string file = AgentData.Inside(project, relative);
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 4 * 1024 * 1024) return null;
            try { return AgentData.Read<T>(file); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static void CollectFlow(FeedbackCase c, DateTimeOffset at, List<FeedbackEvidence> result)
        {
            Action<string,string,string> add = (path, time, excerpt) => result.Add(new FeedbackEvidence {
                Id = "L" + (result.Count + 1), Source = path.Replace('\\','/'), Time = time,
                Text = AgentData.Short(Scrub(excerpt), 14000)
            });
            if (String.IsNullOrWhiteSpace(c.Run))
            {
                var state = ReadSummary<FlowState>(c.Project, FlowCreation.StateFile);
                if (state != null) add(FlowCreation.StateFile, state.Utc ?? c.Occurred,
                    "当前项目进度\n状态 · " + state.Status + "\n阶段 · " + state.Phase + "\n详情 · " + state.Detail);
            }
            string relative = Path.Combine(FlowIncidentStore.RelativeFolder, FlowIncidentStore.LedgerFileName);
            var ledger = ReadSummary<FlowIncidentLedger>(c.Project, relative);
            if (ledger == null || ledger.Incidents == null) return;
            var excerpts = new List<string>();
            foreach (var incident in ledger.Incidents.AsEnumerable().Reverse())
            {
                if (incident == null || (!String.IsNullOrWhiteSpace(c.Run) &&
                    !String.Equals(c.Run.Trim(), incident.RunId, StringComparison.OrdinalIgnoreCase))) continue;
                DateTimeOffset occurred;
                if (incident.Resolved && (!DateTimeOffset.TryParse(incident.OccurredUtc, out occurred) ||
                    Math.Abs((occurred - at).TotalMinutes) > 15)) continue;
                excerpts.Add(AgentData.Short(Scrub(incident.OccurredUtc + " · " +
                    (incident.Resolved ? "已解决" : "未解决") + " · " + incident.StageTitle + " · " + incident.Summary), 700));
                if (excerpts.Count == 20) break;
            }
            if (excerpts.Count > 0) add(relative, c.Occurred, String.Join("\n", excerpts));
        }

        internal static List<FeedbackEvidence> Collect(FeedbackCase c)
        {
            DateTimeOffset at=DateTimeOffset.Parse(c.Occurred);
            var files=new List<string>();
            var result=new List<FeedbackEvidence>();
            if(!String.IsNullOrEmpty(c.Project) && Directory.Exists(c.Project))
            {
                if ((File.GetAttributes(c.Project) & FileAttributes.ReparsePoint) != 0) throw new IOException("反馈项目不能是链接目录。");
                if (c.Stage != "程序") CollectFlow(c, at, result);
                if(c.Stage=="全部"||c.Stage=="剧本分析")files.AddRange(Files(c.Project,@"剧本分析\运行记录","*.log",0));
                if(c.Stage=="全部"||c.Stage=="资产生图")files.AddRange(Files(c.Project,@"Codex助手\运行收据","image-phases.txt",2));
                if(c.Stage=="全部"||c.Stage=="视频生成")
                {
                    files.AddRange(Files(c.Project,VideoDelivery.Folder,"任务.json",1));
                    files.AddRange(Files(c.Project,"视频生成","任务.json",1));
                }
            }
            if(c.Stage=="全部" || c.Stage=="程序" || String.IsNullOrEmpty(c.Project))
            {
                string fault=AgentData.Inside(AgentData.Local,"运行故障日志.log");
                if(File.Exists(fault))files.Add(fault);
            }
            foreach(string f in files.OrderBy(p=>Math.Abs((File.GetLastWriteTimeUtc(p)-at.UtcDateTime).TotalSeconds)))
            {
                if(result.Count>=12)break;
                string shortPath=Path.GetFileName(f)=="运行故障日志.log" ? Path.GetFileName(f) : f.Substring(Path.GetFullPath(c.Project).TrimEnd('\\').Length+1);
                if(!String.IsNullOrWhiteSpace(c.Run) && shortPath.IndexOf(c.Run,StringComparison.OrdinalIgnoreCase)<0)continue;
                if(File.GetLastWriteTimeUtc(f)<at.UtcDateTime.AddMinutes(-15))continue;
                var lines=new List<string>(); int number=0;
                if(shortPath.EndsWith("任务.json"))
                {
                    var r=AgentData.Read<ClipReceipt>(f); DateTimeOffset timestamp;
                    if(r==null || !DateTimeOffset.TryParse(r.Utc,out timestamp) || Math.Abs((timestamp-at).TotalMinutes)>15)continue;
                    lines.Add("行 1（选取回执字段） "+r.Utc+" "+r.Status+" "+r.Error+" "+r.SubmitError+" · 原任务号 "+r.ExternalId);
                }
                else
                {
                    bool fault=shortPath=="运行故障日志.log", keep=false;
                    foreach(var line in File.ReadLines(f))
                    {
                        number++;
                        var match=Regex.Match(line,@"^\[?(\d{4}-\d{2}-\d{2}[ T][\d:.+-]+Z?)");
                        if(match.Success)
                        {
                            DateTimeOffset dt;
                            keep=DateTimeOffset.TryParse(match.Groups[1].Value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out dt)
                                && Math.Abs((dt-at).TotalMinutes)<=15;
                            if(fault && !String.IsNullOrEmpty(c.Project))keep=keep && line.IndexOf(c.Project,StringComparison.OrdinalIgnoreCase)>=0;
                        }
                        if(keep && (!fault || match.Success || line.TrimStart().StartsWith("at ") || line.TrimStart().StartsWith("在 ")))
                        {
                            lines.Add("行 "+number+" "+AgentData.Short(line,700));
                            if(lines.Count>20)lines.RemoveAt(0);
                        }
                    }
                }
                if(lines.Count>0)result.Add(new FeedbackEvidence{Id="L"+(result.Count+1),Source=shortPath.Replace('\\','/'),Text=Scrub(String.Join("\n",lines)),Time=c.Occurred});
            }
            return result;
        }
        internal static string ImagePath(FeedbackCase c,FeedbackEvidence e)
        {
            if(!Regex.IsMatch(e.Image??"",@"\A[a-f0-9]{32}\.png\z"))throw new InvalidDataException("截图文件名无效");
            return Local(c,Path.Combine("附件",e.Image));
        }
        internal static string EvidencePath(FeedbackEvidence e)
        {
            if(!Regex.IsMatch(e.Id??"",@"\A[SL]\d{1,5}\z"))throw new InvalidDataException("证据编号无效");
            return e.Image==null?"日志/"+e.Id+".txt":"截图/"+e.Id+".png";
        }
        static readonly string[] Arrays={"steps","facts","causes","questions"};
        static readonly string[] Values={"stage","expected","actual"};
        internal static FeedbackFinding Analyze(FeedbackCase c,string selected)
        {
            string work=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"lacuna-feedback-"+AgentData.Id())).FullName;
            string schema=Path.Combine(work,"schema.json"), output=Path.Combine(work,"result.json");
            try
            {
                var properties=new Dictionary<string,object>();
                foreach(var k in Arrays)properties[k]=new {type="array",items=new {type="string"}};
                foreach(var k in Values)properties[k]=new {type="string"};
                AgentData.Save(schema,new {type="object",properties=properties,required=properties.Keys.ToArray(),additionalProperties=false});
                string skill;
                using(var r=new StreamReader(typeof(Feedback).Assembly.GetManifestResourceStream("FeedbackSkill")))skill=r.ReadToEnd();
                string input=skill+"\n"+AgentData.Json().Serialize(new {description=Scrub(c.Description),version=Program.Version,stage=c.Stage,occurred=c.Occurred,
                    evidence=c.Evidence.Select(e=>new {id=e.Id,source=Scrub(e.Source),excerpt=Scrub(e.Text),kind=e.Image==null?"日志":"截图（见同序附件）"})});
                var images = c.Evidence.Where(e => e.Image != null).Select(e => ImagePath(c,e)).ToList();
                if (CodexAuthMode.IsApi)
                {
                    string model = CodexAuthMode.Model;
                    c.Codex = "Responses API · " + model;
                    OpenAiResponsesClient.Run(work, input, model, null, schema, output, images, null, null, false,
                        false, null, null, 180000, null, System.Threading.CancellationToken.None);
                }
                else
                {
                    var status = CodexConnection.Check(selected);
                    c.Codex = Scrub(status.Version ?? "未知");
                    if (status.State != CodexState.SignedIn) throw new InvalidOperationException("本机 Codex " + status.Label + "。可在设置中重新连接，也可导出基础报告。");
                    var args=new List<string>{"exec","--ignore-user-config","--skip-git-repo-check","--ephemeral","--sandbox","read-only","-c","approval_policy=\"never\"","-c","web_search=\"disabled\"","--output-schema",schema,"-o",output};
                    foreach(string flag in new[]{"shell_tool","image_generation","hooks","apps","plugins","browser_use","computer_use","multi_agent"})args.AddRange(new[]{"--disable",flag});
                    foreach(string image in images)args.AddRange(new[]{"-i",image});
                    args.Add("-");
                    if(status.Path.EndsWith(".cmd",StringComparison.OrdinalIgnoreCase) && args.Any(x=>Regex.IsMatch(x,@"[%!&|<>^]")))throw new IOException("请在设置中选择原生 codex.exe。");
                    var run=CodexConnection.Run(status.Path,AgentRunner.Args(args),180000,work,input);
                    if(run.Code!=0 || !File.Exists(output))throw new IOException("Codex 未完成分析。"+AgentData.Short(Scrub(run.Error),280));
                }
                var obj=AgentData.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(output));
                if(obj==null || !Arrays.All(k=>obj.ContainsKey(k) && obj[k] is System.Collections.IList && ((System.Collections.IList)obj[k]).Cast<object>().All(v => v is string)) ||
                    !Values.All(k=>obj.ContainsKey(k) && obj[k] is string))throw new InvalidDataException("反馈分析字段不完整。");
                var finding=AgentData.Read<FeedbackFinding>(output);
                foreach(var a in new[]{finding.steps,finding.facts,finding.causes,finding.questions})for(int i=0;i<a.Length;i++)a[i]=Scrub(a[i]);
                finding.expected=Scrub(finding.expected); finding.actual=Scrub(finding.actual); finding.stage=Scrub(finding.stage);
                return finding;
            }
            finally {foreach(var file in new[]{schema,output})if(File.Exists(file))File.Delete(file); if(!Directory.EnumerateFileSystemEntries(work).Any())Directory.Delete(work);}
        }
        internal static void Export(FeedbackCase c)
        {
            var f=c.Finding??new FeedbackFinding{steps=new string[0],expected="待核实",actual=Scrub(c.Description),stage=c.Stage,facts=new string[0],causes=new string[0],questions=new[]{"未完成 Codex 分析，请开发者核对。"}};
            string status=c.Finding==null?"未完成 Codex 分析":c.Status;
            var report=new {schema_version="1.0",feedback_id=c.Id,lacuna_version=Program.Version,skill_version="1.0",windows_version=Environment.OSVersion.VersionString,
                codex_version=c.Codex??"未知",created=c.Created,occurred=c.Occurred,diagnostic_status=status,description=Scrub(c.Description),analysis=f,
                evidence=c.Evidence.Select(e=>new {id=e.Id,path=EvidencePath(e),source=Scrub(e.Source),time=e.Time})};
            var md=new StringBuilder("# Lacuna 问题反馈\n\n").AppendLine(c.Id+" · "+Program.Version+" · "+status).AppendLine("发生时间 "+c.Occurred)
                .AppendLine(report.windows_version+" · Codex "+report.codex_version).AppendLine("\n## 成员描述\n\n"+report.description);
            Action<string,IEnumerable<string>> section=(title,body)=>md.AppendLine("\n## "+title+"\n\n"+String.Join("\n",(body??new string[0]).DefaultIfEmpty("待核实").Select(x=>"- "+x)));
            section("复现步骤",f.steps); section("预期与实际",new[]{f.expected,f.actual});section("问题环节",new[]{f.stage});
            section("已证实的事实",f.facts); section("可能原因",f.causes);section("待核实项",f.questions);
            section("证据",c.Evidence.Select(e=>"["+e.Id+"]("+EvidencePath(e)+") "+Scrub(e.Source)+" "+e.Time).DefaultIfEmpty("没有选入匹配的日志或截图。"));
            string target=Zip(c);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string tmp=target+"."+AgentData.Id();
            try
            {
                using(var fs=new FileStream(tmp,FileMode.CreateNew))using(var z=new ZipArchive(fs,ZipArchiveMode.Create,false,Encoding.UTF8))
                {
                    Action<string,string> add=(name,value)=>{using(var w=new StreamWriter(z.CreateEntry(name).Open(),new UTF8Encoding(false)))w.Write(value);};
                    add("反馈报告.md",md.ToString());add("report.json",AgentData.Json().Serialize(report));
                    z.CreateEntry("日志/");z.CreateEntry("截图/");
                    foreach(var e in c.Evidence)if(e.Image==null)add(EvidencePath(e),Scrub(e.Source)+"\n"+Scrub(e.Text));
                    else using(var source=File.OpenRead(ImagePath(c,e)))using(var dest=z.CreateEntry(EvidencePath(e)).Open())source.CopyTo(dest);
                }
                if(File.Exists(target))File.Replace(tmp,target,null);else File.Move(tmp,target);
            }
            finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
    }
}

