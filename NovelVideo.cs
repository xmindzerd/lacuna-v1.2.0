using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JsonMap = System.Collections.Generic.Dictionary<string,object>;

namespace OneClickFilm
{
    public sealed class ClipPrice
    {
        public string ClipKey, NodeKey, NodeId, ConfigHash, Title, Model, Resolution, ContentKey;
        public decimal Credits;
    }

    public sealed class VideoQuote
    {
        public string ProjectId, ProjectName, Team, Ratio, Resolution, Model, Utc, FilmRevision;
        public decimal Balance, Total;
        public List<ClipPrice> Items=new List<ClipPrice>();
        public decimal Remaining { get { return Balance-Total; } }
    }

    internal sealed class NovelVideo
    {
        readonly string root, folder, exe;
        readonly CancellationToken token;
        internal NovelVideo(string project,CancellationToken cancel)
        {
            root=project; token=cancel;
            folder=Directory.CreateDirectory(AgentData.Inside(root,Path.Combine(VideoDelivery.Folder,"平台画布"))).FullName;
            exe=Find();
            if(exe==null)throw new FileNotFoundException("需要安装即梦短剧 CLI（jimeng-novel），才能取得平台报价。");
        }

        internal static string Find()
        {
            return new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\jimeng-novel\jimeng-novel.exe"),
                CodexConnection.FindOnPath("jimeng-novel.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\jimeng-novel.exe")
            }.FirstOrDefault(p => !String.IsNullOrEmpty(p) && File.Exists(p));
        }

        JsonMap Call(params string[] args)
        {
            return CallWithTimeout(150000,args);
        }
        sealed class PlatformError : IOException
        {
            internal readonly string Code;
            internal readonly bool Temporary;
            internal bool NotSubmitted;
            internal PlatformError(string command,string code,string message,bool retryable):base(command+" · "+code+" "+AgentData.Short(Regex.Replace(message,@"https?://[^\s""]+","[平台地址]"),420)){
                Code=code;
                Temporary=retryable || code=="UPLOAD_TIMEOUT" || Regex.IsMatch(message,
                    @"(?i)timeout|timed out|connection (?:reset|aborted|refused)|unexpected EOF|temporary failure|\b(?:429|502|503|504)\b|等待响应超时");
            }
        }
        JsonMap CallWithTimeout(int timeout,params string[] args)
        {
            token.ThrowIfCancellationRequested();
            var clock=Stopwatch.StartNew();
            var sent=CodexConnection.Run(exe,AgentRunner.Args(args.Concat(new[]{"-j","--full"})),timeout,folder,cancellation:token,useSystemProxy:true);
            bool submit=args.Length>=2 && args[0]=="node" && args[1]=="run";
            if(FlowCreation.Development)DevMonitor.Log(root,"MEDIA/OCR",String.Join(" ",args.Take(2))+" · "+clock.ElapsedMilliseconds+" ms · exit "+sent.Code);
            if(submit){
                string archive=AgentData.Inside(root,Path.Combine(VideoDelivery.Folder,"费用确认","提交回执-"+Guid.NewGuid().ToString("N")+".json"));
                try {AgentData.Save(archive,new {exitCode=sent.Code,stdout=CodexAuthMode.RedactDiagnostic(sent.Output),stderr=CodexAuthMode.RedactDiagnostic(sent.Error)});}
                catch(IOException){DevMonitor.Log(root,"MEDIA/OCR","提交原始回执未能归档，继续保存逐段任务编号。");}
                catch(UnauthorizedAccessException){DevMonitor.Log(root,"MEDIA/OCR","提交原始回执目录不可写，继续保存逐段任务编号。");}
            }
            JsonMap envelope=null;
            try{envelope=AgentData.Json().Deserialize<JsonMap>(sent.Output);}catch(ArgumentException){}catch(InvalidOperationException){}
            var state=Str(envelope,"status");
            var payload=Val(envelope,"data") as JsonMap;
            bool partial=(submit || args.Take(2).SequenceEqual(new[]{"node","result"})) &&
                (ResultRows(payload).Any() || Val(envelope,"data") is IList || !String.IsNullOrEmpty(Str(payload,"key")));
            if(!partial && (sent.Code!=0 || !(state=="success" || state=="running"))){
                var err=Val(envelope,"error") as JsonMap;
                string msg=(Str(err,"message")+" "+Str(err,"hint")+" "+Str(envelope,"hint")).Trim();
                if((msg+" "+sent.Error).IndexOf("requires Node.js 18",StringComparison.OrdinalIgnoreCase)>=0)
                    throw new IOException("即梦编辑画布需要 Node.js 18 或更高版本，请检查随包 tools\\node 或本机 Node.js 运行时。");
                throw new PlatformError(args[0]+" "+(args.Length>1?args[1]:""),Str(err,"code"),msg.Length>0?msg:sent.Error,Object.Equals(Val(err,"retryable"),true))
                    { NotSubmitted=ExplicitRejection(err) || ExplicitRejection(payload) || ExplicitRejection(envelope) };
            }
            var data=Val(envelope,"data") as JsonMap;
            if(data==null && Val(envelope,"data") is IList)data=new JsonMap{{"items",Val(envelope,"data")}};
            if(data==null)throw new InvalidDataException("即梦回执缺少数据。");
            data["_team"]=Str(Val(envelope,"context") as JsonMap,"teamId");
            return data;
        }
        static object Val(JsonMap row,string key){object value;return row!=null && row.TryGetValue(key,out value)?value:null;}
        static string Str(JsonMap row,string key){return Convert.ToString(Val(row,key),CultureInfo.InvariantCulture);}
        static List<JsonMap> Rows(JsonMap row,string key)
        {
            var list=Val(row,key) as IList;
            return list==null?new List<JsonMap>():list.Cast<object>().OfType<JsonMap>().ToList();
        }
        static IEnumerable<JsonMap> ResultRows(JsonMap data)
        {
            return new[]{"items","results","nodes","tasks"}.SelectMany(k=>Rows(data,k));
        }
        static bool ExplicitRejection(JsonMap data)
        {
            return Object.Equals(Val(data,"submitted"),false) || Str(data,"code")=="SUBMIT_REJECTED";
        }
        static string RowError(JsonMap row)
        {
            var error=Val(row,"error") as JsonMap;
            string message=error==null?Str(row,"error"):Str(error,"message");
            if(error!=null && message.Length==0)message=Str(error,"code");
            return CodexAuthMode.RedactDiagnostic(message);
        }
        static decimal Amount(JsonMap row,string field)
        {
            decimal value;
            if(!VideoDelivery.ParseCredits(Str(row,field),out value))throw new InvalidDataException("平台未返回有效积分："+field);
            return value;
        }

        private bool HasActiveCanvasTasks()
        {
            string videoDir = AgentData.Inside(root, VideoDelivery.Folder);
            if (!Directory.Exists(videoDir)) return false;
            foreach (string sub in Directory.EnumerateDirectories(videoDir))
            {
                string taskFile = Path.Combine(sub, "任务.json");
                if (!File.Exists(taskFile)) continue;
                ClipReceipt receipt = null;
                try { receipt = AgentData.Read<ClipReceipt>(taskFile); }
                catch(Exception e) { throw new IOException("无法核实本地视频任务，暂不能重绑即梦画布。",e); }
                if (receipt != null && receipt.Provider == "novel" && receipt.Status != "平台失败" && !Returned(root,receipt))
                    return true;
            }
            return false;
        }

        string Canvas()
        {
            string binding=Path.Combine(folder,".jimeng","config.json");
            if(!File.Exists(binding)){
                if(HasActiveCanvasTasks())throw new IOException("本地视频任务仍需查询。请恢复原画布并切回原即梦账号或空间查询回片。");
                var result=Call("init","--title","Lacuna · "+Path.GetFileName(root));
                if(Str(result,"projectId").Length==0)throw new InvalidDataException("画布创建后缺少项目编号。");
                return Str(result,"projectId");
            }
            JsonMap config = null;
            try { config=AgentData.Read<JsonMap>(binding); }
            catch { }
            string id=Str(config,"projectId");
            if(id.Length==0){
                try { var canvas=Call("canvas","get");id=Str(canvas,"projectId"); }
                catch (PlatformError) { id = ""; }
            }
            if(id.Length>0){
                try
                {
                    Call("canvas","get","--project-id",id);
                    return id;
                }
                catch(PlatformError e)
                {
                    if(e.Code!="TARGET_NOT_FOUND" && e.Code!="PROJECT_NOT_FOUND" && e.Code!="NOT_FOUND" &&
                       e.Code!="FORBIDDEN" && e.Code!="PERMISSION_DENIED") throw;
                }
            }
            if(HasActiveCanvasTasks())throw new IOException("当前即梦账号无法访问原画布，且仍有未取回任务。请切回原即梦账号或空间查询回片。");
            var rebound=Call("init","--force","--title","Lacuna · "+Path.GetFileName(root));
            string freshId=Str(rebound,"projectId");
            if(freshId.Length==0)throw new InvalidDataException("重新绑定即梦画布后缺少项目编号。");
            return freshId;
        }

        // Only image upload and resource lookup use automatic retries. Paid commands always use Call directly.
        JsonMap ImageRequest(string label,Action<string> progress,Func<JsonMap> request)
        {
            for(int attempt=1;;attempt++){
                token.ThrowIfCancellationRequested();
                try{return request();}
                catch(Exception e){
                    var platform=e as PlatformError;
                    if(!(e is TimeoutException || platform!=null && platform.Temporary))throw;
                    if(attempt>=3)throw new IOException(label+" · 即梦素材连接连续 3 次失败，准备工作已暂停。恢复后重试本段即可。 "+e.Message);
                    int seconds=attempt==1?2:6;
                    progress(label+" · 即梦素材连接暂时失败，"+seconds+" 秒后第 "+(attempt+1)+"/3 次尝试");
                    if(token.WaitHandle.WaitOne(seconds*1000))token.ThrowIfCancellationRequested();
                }
            }
        }

        string PrepareImage(string directory,PromptReference reference,string label,Action<string> progress,Dictionary<string,string> ready)
        {
            string path=AssetProduction.Verify(root,new AssetAttempt{Path=reference.Path,Digest=reference.Digest});
            string digest=reference.Digest.ToUpperInvariant(),id;
            if(ready.TryGetValue(digest,out id))return id;
            string checkpoint=Path.Combine(directory,digest+".json");
            id=Str(AgentData.Read<JsonMap>(checkpoint),"assetId");
            JsonMap resource=null;
            Func<JsonMap> lookup=()=>ImageRequest(label,progress,()=>Call("resource","get","--asset-id",id));
            if(id.Length>0){
                progress(label+" · 核对已上传的参考图");
                try{resource=lookup();}catch(PlatformError e){if(e.Code!="TARGET_NOT_FOUND" && e.Code!="RESOURCE_NOT_FOUND")throw;}
            }
            if(resource==null){
                progress(label+" · 正在上传参考图");
                int timeout=(int)Math.Min(1200000,180000+(new FileInfo(path).Length/1048576)*4000);
                resource=ImageRequest(label,progress,()=>CallWithTimeout(timeout,"resource","upload","--file",path,"--wait","30"));
                id=Str(resource,"assetId");
                if(id.Length==0 || Str(resource,"type")!="image")throw new InvalidDataException(label+" · 即梦未返回图片资源编号。");
                AgentData.Save(checkpoint,new JsonMap{{"assetId",id}});
            }
            var watch=Stopwatch.StartNew();
            while(Str(resource,"status")=="processing" && watch.Elapsed.TotalSeconds<90){
                progress(label+" · 已上传，正在等待即梦处理");
                if(token.WaitHandle.WaitOne(3000))token.ThrowIfCancellationRequested();
                resource=lookup();
            }
            if(Str(resource,"assetId")!=id || Str(resource,"type")!="image" || Str(resource,"status")!="ready")
                throw new InvalidDataException(label+" · 即梦素材状态 "+Str(resource,"status")+"，已保存编号，可稍后核对。");
            if(FlowCreation.Development)DevMonitor.Log(root,"MEDIA/OCR","参考图已就绪 · "+id);
            ready.Add(digest,id);return id;
        }

        internal VideoQuote Prepare(VideoOptions options,string ratio,List<VideoClip> clips,Action<string> progress)
        {
            foreach(var clip in clips)
                if(clip.Model==null){clip.Model=VideoPlatform.Canonical(options.Model);clip.Resolution=options.Resolution;}
            return PrepareSelected(options,ratio,clips,progress);
        }

        internal VideoQuote Prepare(string ratio,List<VideoClip> clips,Action<string> progress)
        {
            var cfg=FlowCreation.Config(root);
            if(cfg==null || cfg.Video==null || !cfg.Video.Enabled)throw new InvalidOperationException("项目未开启视频生成。");
            return PrepareSelected(cfg.Video,ratio,clips,progress);
        }

        VideoQuote PrepareSelected(VideoOptions options,string ratio,List<VideoClip> clips,Action<string> progress)
        {
            CheckedBook(ratio);
            if(clips.Count==0)throw new InvalidDataException("请选择待生成片段。");
            var catalog=VideoPlatform.Load(token);
            foreach(var clip in clips)ValidateClip(catalog,clip,ratio);
            if(catalog.Offline)throw new InvalidDataException("无法核实当前模型目录。"+catalog.LoadError);
            var models=Rows(Call("model","list","--type","video"),"models");
            foreach(var clip in clips)
                if(!models.Any(m=>String.Equals(Str(m,"modelId"),clip.Model,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(clip.Title+" · 当前即梦账号的模型目录中没有 "+clip.Model+"。");
            VideoQuote q;
            using(QuoteLock())
            using(new ProjectTaskLease(root,"影片制作"))
            {
                var book=CheckedBook(ratio);
                RejectReceipts(book,options,clips);
                q=PrepareNodes(ratio,clips,progress);
                if(book!=null)q.FilmRevision=book.Revision;
            }
            Price(q);
            return q;
        }

        FileStream QuoteLock()
        {
            var clock=Stopwatch.StartNew();
            while(true)
            {
                token.ThrowIfCancellationRequested();
                try { return FlowCreation.Lock(root); }
                catch(IOException){if(clock.ElapsedMilliseconds>=10000)throw;}
                token.WaitHandle.WaitOne(100);
            }
        }

        FilmBook CheckedBook(string ratio)
        {
            if(FlowCreation.Config(root)==null)return null;
            var book=Film.Open(root);
            VideoDelivery.RequireAlignedRatio(root,book.Brief.Ratio);
            if(book.Brief.Ratio!=ratio)throw new InvalidDataException("项目画幅已变化，请重新报价。");
            return book;
        }

        void RejectReceipts(FilmBook book,VideoOptions options,List<VideoClip> clips)
        {
            var matched=VideoDelivery.FindReceipts(root,book,options,clips);
            foreach(var clip in clips)
                if((clip.ContentKey!=null && matched.ContainsKey(clip.ContentKey)) || Read(root,clip.Key)!=null)
                    throw new IOException(clip.Title+" 已有平台记录，请刷新后核对。");
        }

        internal static void ValidateClip(VideoPlatform catalog,VideoClip clip,string ratio)
        {
            VideoModel model;
            try {model=catalog.Require(new VideoOptions{Model=clip.Model,Resolution=clip.Resolution},ratio);}
            catch(InvalidDataException e){throw new InvalidDataException(clip.Title+" · "+e.Message);}
            if(clip.Seconds<model.Min || clip.Seconds>model.Max)
                throw new InvalidDataException(clip.Title+" · 时长 "+clip.Seconds+" 秒，"+model.Name+" 支持 "+model.Min+"–"+model.Max+" 秒。");
            if(clip.Images.Count>model.Images)
                throw new InvalidDataException(clip.Title+" · 参考图 "+clip.Images.Count+" 张，"+model.Name+" 最多支持 "+model.Images+" 张。");
        }

        VideoQuote PrepareNodes(string ratio,List<VideoClip> clips,Action<string> progress)
        {
            string id=Canvas();
            var binding=AgentData.Read<JsonMap>(Path.Combine(folder,".jimeng","config.json"));
            var scope=AgentData.Hash(id+"|"+AssetProduction.StableJson(Val(binding,"space")));
            var directory=Path.Combine(folder,"参考图上传记录",scope);
            var ready=new Dictionary<string,string>();
            var distinctModels=clips.Select(c=>c.Model).Distinct().ToArray();
            var sizes=clips.Select(c=>c.Resolution).Distinct().ToArray();
            var q=new VideoQuote{ProjectId=id,ProjectName=Path.GetFileName(root),Ratio=ratio,
                Model=distinctModels.Length==1?distinctModels[0]:"多模型（"+String.Join("、",distinctModels)+"）",
                Resolution=sizes.Length==1?sizes[0]:"逐段画质"};
            foreach(var clip in clips){
                token.ThrowIfCancellationRequested();
                if(Read(root,clip.Key)!=null)throw new IOException(clip.Title+" 已有平台记录，请刷新后核对。");
                progress("正在搭建平台草稿 · "+clip.Title);
                var cp=new ClipPrice{ClipKey=clip.Key,NodeKey="Lacuna/"+clip.Key,Title=clip.Title,Model=clip.Model,Resolution=clip.Resolution,ContentKey=clip.ContentKey};
                string alternate="Lacuna/video/"+clip.Key;
                CheckBeforeCreate(q,cp,cp.NodeKey);
                CheckBeforeCreate(q,cp,alternate);
                string prompt=Regex.Replace(clip.Prompt,@"@Image(\d+)",m=>"{{Image"+m.Groups[1].Value+"}}");
                string name=clip.Key+".md";
                File.WriteAllText(Path.Combine(folder,name),prompt,new UTF8Encoding(false));
                var args=new List<string>{"node","create","--project-id",id,"--key",cp.NodeKey,"--name",clip.Title,
                    "--type","video","--model",cp.Model,"--prompt-file",name,"--param","ratio="+ratio,
                    "--param","duration="+clip.Seconds};
                if(!String.IsNullOrWhiteSpace(cp.Resolution))args.AddRange(new[]{"--param","resolution="+cp.Resolution});
                foreach(var r in clip.Images){
                    string label=clip.Title+" · "+r.Label+" "+r.Name;
                    string asset=PrepareImage(directory,r,label,progress,ready);
                    args.Add("--ref");args.Add(r.Label.Substring(1)+"=resource:"+asset);
                }
                if(clip.Images.Count==0)args.Add("--clear-refs");
                var created=Call(args.ToArray());cp.NodeId=Str(created,"nodeId");
                if(cp.NodeId.Length==0 || Str(created,"key")!=cp.NodeKey)throw new InvalidDataException("即梦没有确认目标草稿节点："+clip.Title);
                cp.ConfigHash=NodeConfig(q,cp);
                var generation=Val(Val(created,"config") as JsonMap,"generation") as JsonMap;
                if(Val(generation,"count")!=null){
                    cp.NodeKey=alternate;
                    CheckBeforeCreate(q,cp,alternate);
                    args[args.IndexOf("--key")+1]=cp.NodeKey;
                    created=Call(args.ToArray());
                    cp.NodeId=Str(created,"nodeId");
                    generation=Val(Val(created,"config") as JsonMap,"generation") as JsonMap;
                    if(cp.NodeId.Length==0 || Str(created,"key")!=cp.NodeKey || Val(generation,"count")!=null)
                        throw new InvalidDataException("即梦未能建立兼容的视频草稿，请检查节点参数。");
                    cp.ConfigHash=NodeConfig(q,cp);
                }
                q.Items.Add(cp);
            }
            return q;
        }

        void CheckBeforeCreate(VideoQuote quote,ClipPrice clip,string key)
        {
            JsonMap node;
            try { node=Call("node","get","--project-id",quote.ProjectId,"--key",key); }
            catch(PlatformError error){
                if(error.Code=="TARGET_NOT_FOUND" || error.Code=="NODE_NOT_FOUND" || error.Code=="NOT_FOUND")return;
                throw;
            }
            CheckNodeState(quote,clip,key,node);
        }

        void CheckNodeState(VideoQuote quote,ClipPrice clip,string key,JsonMap node)
        {
            string nodeId=Str(node,"nodeId");
            if(nodeId.Length==0 || Str(node,"key")!=key)throw new InvalidDataException("无法核对原即梦节点，请刷新画布后再试。");
            var state=Val(node,"state") as JsonMap;
            string status=Str(state,"status"),run=Str(state,"runId");
            if(status=="failed" || status=="idle" && run.Length==0)return;
            if(status=="submitted" || status=="running" || status=="succeeded" || run.Length>0){
                var found=new ClipReceipt{Key=clip.ClipKey,Title=clip.Title,Provider="novel",Canvas=quote.ProjectId,
                    ContentKey=clip.ContentKey,Node=key,NodeId=nodeId,ExternalId=run,Status=run.Length==0 || status=="idle"?"待核对":"已提交"};
                VideoDelivery.Save(VideoDelivery.ReceiptPath(root,clip.ClipKey),found);
                throw new IOException(clip.Title+" 在即梦已有生成记录，已绑定原任务。请查询回片。");
            }
            throw new InvalidDataException("平台原节点状态不明，请核对后再试。");
        }

        string NodeConfig(VideoQuote quote,ClipPrice clip)
        {
            var data=Call("node","get","--project-id",quote.ProjectId,"--key",clip.NodeKey);
            CheckNodeState(quote,clip,clip.NodeKey,data);
            var config=Val(data,"config");
            if(Str(data,"nodeId")!=clip.NodeId || config==null)throw new InvalidDataException("平台草稿已变化，请重新报价。");
            return AgentData.Hash(AssetProduction.StableJson(config));
        }

        void Price(VideoQuote q)
        {
            var args=new List<string>{"credit","estimate","--project-id",q.ProjectId};
            foreach(var item in q.Items){args.Add("--key");args.Add(item.NodeKey);}
            var reply=Call(args.ToArray());
            string team=Str(reply,"_team");
            if(q.Team!=null && q.Team!=team)throw new InvalidOperationException("即梦计费空间已变化，请重新确认报价。");
            q.Team=team;
            ApplyPrice(q,reply);
        }
        internal static void ApplyPrice(VideoQuote q,JsonMap result)
        {
            var rows=Rows(result,"items");
            if(q.Items.Count==0 || rows.Count!=q.Items.Count || Amount(result,"unknownCount")!=0)
                throw new InvalidDataException("平台未报出全部所选片段的价格，请刷新报价。");
            foreach(var item in q.Items){
                var row=rows.SingleOrDefault(r=>Str(r,"key")==item.NodeKey && Str(r,"nodeId")==item.NodeId);
                if(row==null || Str(row,"priceStatus")!="available" || !String.Equals(Str(row,"modelId"),item.Model,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(item.Title+" 尚无可用的平台报价。");
                item.Credits=Amount(row,"estimatedCredits");
            }
            q.Total=Amount(result,"total");
            var wallet=Val(result,"balance") as JsonMap;
            q.Balance=wallet==null?Amount(result,"balance"):Amount(wallet,"total");
            if(q.Total!=q.Items.Sum(p=>p.Credits))throw new InvalidDataException("逐段费用与平台合计不一致，请重新报价。");
            q.Utc=AnalysisData.Now();
        }

        internal static ClipReceipt Read(string root,string key)
        {
            var receipt=AgentData.Read<ClipReceipt>(VideoDelivery.ReceiptPath(root,key));
            if(receipt!=null && String.IsNullOrEmpty(receipt.Key))receipt.Key=key;
            return receipt;
        }

        internal static bool Returned(string root,ClipReceipt receipt)
        {
            if(receipt==null || receipt.Status!="已取回" || String.IsNullOrEmpty(receipt.Output))return false;
            string file=AgentData.Inside(root,receipt.Output);return File.Exists(file) && String.Equals(SystemData.Digest(file),receipt.Digest,StringComparison.OrdinalIgnoreCase);
        }

        internal void Submit(VideoQuote confirmed,List<VideoClip> clips,Action<string> progress)
        {
            using(FlowCreation.Lock(root))
            using(new ProjectTaskLease(root,"影片制作")) SubmitLocked(confirmed,clips,progress);
        }

        internal void SubmitLocked(VideoQuote confirmed,List<VideoClip> clips,Action<string> progress)
        {
            var cfg=FlowCreation.Config(root);
            var book=CheckedBook(confirmed.Ratio);
            if(book!=null && book.Revision!=confirmed.FilmRevision)
                throw new InvalidDataException("定稿已经更改，请重新报价。");
            RejectReceipts(book,cfg==null?new VideoOptions():cfg.Video,clips);
            if(!confirmed.Items.Select(i=>i.ContentKey).SequenceEqual(clips.Select(c=>c.ContentKey)) ||
                !confirmed.Items.Select(i=>i.Model).SequenceEqual(clips.Select(c=>c.Model)) ||
                !confirmed.Items.Select(i=>i.Resolution).SequenceEqual(clips.Select(c=>c.Resolution)))
                throw new InvalidOperationException("片段模型或分辨率已经更改，请重新报价。");
            if(!confirmed.Items.Select(x=>x.ClipKey).SequenceEqual(clips.Select(c=>c.Key)))
                throw new InvalidOperationException("选段或定稿已有变化，请重新报价。");
            var now=AgentData.Json().Deserialize<VideoQuote>(AgentData.Json().Serialize(confirmed));
            foreach(var item in now.Items){
                if(Read(root,item.ClipKey)!=null || NodeConfig(now,item)!=item.ConfigHash)
                    throw new IOException(item.Title+" 已变化，请重新报价。");
            }
            Price(now);
            if(now.Remaining<0 || now.Balance!=confirmed.Balance || now.Total!=confirmed.Total ||
                !now.Items.Select(i=>i.Credits).SequenceEqual(confirmed.Items.Select(i=>i.Credits)))
                throw new InvalidOperationException("平台余额或报价已变化，请重新确认。");
            var latest=CheckedBook(confirmed.Ratio);
            if(latest!=null && latest.Revision!=confirmed.FilmRevision)
                throw new InvalidDataException("定稿已经更改，请重新报价。");
            string batch=Guid.NewGuid().ToString("N");
            AgentData.Save(AgentData.Inside(root,Path.Combine(VideoDelivery.Folder,"费用确认",batch+".json")),now);
            for(int start=0;start<now.Items.Count;start+=5){
                token.ThrowIfCancellationRequested();
                var receipts=new List<ClipReceipt>();
                foreach(var item in now.Items.Skip(start).Take(5)){
                    var clip=clips.Single(c=>c.Key==item.ClipKey);
                    receipts.Add(new ClipReceipt{Key=clip.Key,Title=clip.Title,Seconds=clip.Seconds,Provider="novel",
                        ContentKey=clip.ContentKey,Model=item.Model,Resolution=item.Resolution,Ratio=now.Ratio,
                        Canvas=now.ProjectId,Node=item.NodeKey,NodeId=item.NodeId,ChargeBatch=batch,Status="正在提交"});
                }
                foreach(var receipt in receipts)VideoDelivery.Save(VideoDelivery.ReceiptPath(root,receipt.Key),receipt);
                progress("提交视频 · "+String.Join("、",receipts.Select(r=>r.Title)));
                JsonMap data=null;
                string error=null; bool batchRejected=false;
                try { data=Call(BatchArgs("run",now.ProjectId,receipts).ToArray()); }
                catch(Exception e){
                    var platform=e as PlatformError;
                    batchRejected=platform!=null && platform.NotSubmitted;
                    error=e is OperationCanceledException?"提交期间已取消，请核对原任务。":CodexAuthMode.RedactDiagnostic(e.Message);
                }
                bool unkeyedBatch=data!=null && Str(data,"key").Length==0 && !ResultRows(data).Any();
                if(unkeyedBatch){
                    batchRejected=ExplicitRejection(data) || ExplicitRejection(Val(data,"error") as JsonMap);
                    string detail=RowError(data);
                    if(detail.Length>0)error=detail;
                }
                foreach(var receipt in receipts){
                    var row=MatchRow(data,receipt);
                    receipt.ExternalId=Str(row,"runId");
                    string status=Str(row,"status");
                    if(!String.IsNullOrWhiteSpace(receipt.ExternalId)){
                        receipt.Status=status=="failed"?"平台失败":"已提交";
                        if(status=="failed")receipt.SubmitError=RowError(row);
                    }else{
                        bool rejected=batchRejected || ExplicitRejection(row) || ExplicitRejection(Val(row,"error") as JsonMap) || Str(row,"error")=="SUBMIT_REJECTED";
                        receipt.Status=rejected?"平台失败":"待核对";
                        string detail=RowError(row);
                        receipt.SubmitError=detail.Length>0?detail:error??(rejected?"平台已明确拒绝提交。":"即梦未返回匹配的任务号，请查询原任务。");
                    }
                    VideoDelivery.Save(VideoDelivery.ReceiptPath(root,receipt.Key),receipt);
                }
                token.ThrowIfCancellationRequested();
                if(data==null || unkeyedBatch){
                    int skipped=now.Items.Count-start-receipts.Count;
                    if(skipped>0)progress("本批提交未完成，后续 "+skipped+" 段尚未提交。");
                    break;
                }
            }
        }

        private static List<string> BatchArgs(string command,string canvas,IEnumerable<ClipReceipt> receipts)
        {
            var args=new List<string>{"node",command,"--project-id",canvas};
            foreach(var r in receipts){args.Add("--key");args.Add(r.Node);}
            return args;
        }

        static JsonMap MatchRow(JsonMap data,ClipReceipt r)
        {
            var matches=ResultRows(data).DefaultIfEmpty(data)
                .Where(m=>m!=null && Str(m,"key")==r.Node && (Str(m,"nodeId").Length==0 || String.IsNullOrEmpty(r.NodeId) || Str(m,"nodeId")==r.NodeId)).Take(2).ToArray();
            return matches.Length==1?matches[0]:null;
        }
        private static string DownloadPath(string dir,JsonMap row,ClipReceipt receipt)
        {
            string local=Str(row,"localPath");
            string full=String.IsNullOrWhiteSpace(local)?null:Path.GetFullPath(Path.IsPathRooted(local)?local:Path.Combine(dir,local));
            if(full!=null && !full.StartsWith(dir+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载回执指向批次目录之外。");
            if(full!=null && File.Exists(full))return full;
            string name=Path.GetFileName(receipt.Node.Replace('/',Path.DirectorySeparatorChar))+".mp4";
            var files=Directory.GetFiles(dir,"*.mp4",SearchOption.AllDirectories).Where(p=>String.Equals(Path.GetFileName(p),name,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(files.Length!=1)throw new IOException("平台尚未下载可辨认的本段回片。");
            return files[0];
        }

        static void CommitFile(string source,string destination)
        {
            if(File.Exists(destination))File.Replace(source,destination,null);
            else File.Move(source,destination);
        }

        static void InstallDownload(string source,string destination,string digest)
        {
            if(String.Equals(Path.GetPathRoot(source),Path.GetPathRoot(destination),StringComparison.OrdinalIgnoreCase)){
                try { CommitFile(source,destination); return; }
                catch(IOException){} catch(UnauthorizedAccessException){}
            }
            string staging=Path.Combine(Path.GetDirectoryName(destination),"未完成回片.mp4");
            File.Copy(source,staging,true);
            if(!String.Equals(SystemData.Digest(staging),digest,StringComparison.OrdinalIgnoreCase))
                throw new IOException("回片复制校验失败，原始下载已保留。");
            CommitFile(staging,destination);
            try { File.Delete(source); } catch(IOException){} catch(UnauthorizedAccessException){}
        }

        static void CleanEmptyDirectories(string dir)
        {
            try {
                if(!Directory.Exists(dir))return;
                foreach(string sub in Directory.GetDirectories(dir))
                    if((File.GetAttributes(sub)&FileAttributes.ReparsePoint)==0)CleanEmptyDirectories(sub);
                if(!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }

        bool FinishDownload(ClipReceipt receipt,string receiptPath)
        {
            if(String.IsNullOrEmpty(receipt.PendingDownload))return false;
            string temp=AgentData.Inside(root,receipt.PendingDownload);
            string batchPrefix=Path.Combine(folder,"回片-");
            int separator=temp.IndexOf(Path.DirectorySeparatorChar,Math.Min(batchPrefix.Length,temp.Length));
            Guid batchId;
            if(!temp.StartsWith(batchPrefix,StringComparison.OrdinalIgnoreCase) || separator<0 ||
                !Guid.TryParseExact(temp.Substring(batchPrefix.Length,separator-batchPrefix.Length),"N",out batchId) ||
                !temp.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("暂存回片路径与平台目录不匹配。");
            string dest=Path.Combine(Path.GetDirectoryName(receiptPath),"回片.mp4");
            if(!File.Exists(dest) || !String.Equals(SystemData.Digest(dest),receipt.Digest,StringComparison.OrdinalIgnoreCase)){
                if(!File.Exists(temp)){receipt.PendingDownload=null;return false;}
                if(!String.Equals(SystemData.Digest(temp),receipt.Digest,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("暂存回片校验失败。");
                InstallDownload(temp,dest,receipt.Digest);
            }
            receipt.Status="已取回";receipt.Error=null;receipt.PendingDownload=null;
            receipt.Output=dest.Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length+1);
            VideoDelivery.Save(receiptPath,receipt);
            string staging=Path.Combine(Path.GetDirectoryName(dest),"未完成回片.mp4");
            try {
                if(File.Exists(temp) && SystemData.Digest(temp)==receipt.Digest)File.Delete(temp);
                if(File.Exists(staging) && SystemData.Digest(staging)==receipt.Digest)File.Delete(staging);
            }catch(IOException){}catch(UnauthorizedAccessException){}
            CleanEmptyDirectories(temp.Substring(0,separator));
            return true;
        }

        static bool PollFailure(ClipReceipt receipt,Exception error,Dictionary<string,int> errors)
        {
            int count;errors.TryGetValue(receipt.Key,out count);errors[receipt.Key]=++count;
            var platform=error as PlatformError;
            bool hasDownload=!String.IsNullOrEmpty(receipt.PendingDownload);
            bool retry=hasDownload || (platform!=null?platform.Temporary:error is IOException || error is TimeoutException);
            receipt.Error=AgentData.Short(CodexAuthMode.RedactDiagnostic(error.Message),350);
            receipt.Status=retry && (count<3 || hasDownload)?"正在重查":"待核对";
            return retry && count<3;
        }

        internal void Collect(List<VideoClip> clips,string ratio,string resolution,Action<string> progress,int queryTimeoutMs=150000)
        {
            var pending=clips.Where(c=>{var r=Read(root,c.Key);return r!=null && r.Provider=="novel" && !Returned(root,r) && r.Status!="平台失败";}).ToList();
            var watch=Stopwatch.StartNew();
            var errors=new Dictionary<string,int>();
            var maxWait=TimeSpan.FromMinutes(45);
            while(pending.Count>0 && watch.Elapsed<maxWait){
                var poll=new List<ClipReceipt>();
                foreach(var clip in pending.ToArray()){
                    token.ThrowIfCancellationRequested();
                    string path=VideoDelivery.ReceiptPath(root,clip.Key);
                    var receipt=Read(root,clip.Key);
                    try {
                        if(FinishDownload(receipt,path))pending.Remove(clip);
                        else poll.Add(receipt);
                    } catch(Exception error){
                        if(!PollFailure(receipt,error,errors))pending.Remove(clip);
                        VideoDelivery.Save(path,receipt);
                    }
                }
                foreach(var canvas in poll.GroupBy(r=>r.Canvas)){
                    var group=canvas.ToArray();
                    for(int start=0;start<group.Length;start+=5){
                        token.ThrowIfCancellationRequested();
                        var batch=group.Skip(start).Take(5).ToArray();
                        string dir=Directory.CreateDirectory(Path.Combine(folder,"回片-"+Guid.NewGuid().ToString("N"))).FullName;
                        try{
                            var args=BatchArgs("result",canvas.Key,batch);
                            args.Add("--out");args.Add(dir+Path.DirectorySeparatorChar);
                            progress("查询回片 · "+String.Join("、",batch.Select(r=>r.Title)));
                            JsonMap data=null;Exception failure=null;
                            try{data=CallWithTimeout(queryTimeoutMs,args.ToArray());}catch(OperationCanceledException){throw;}catch(Exception e){failure=e;}
                            var taken=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach(var receipt in batch){
                                var clip=pending.First(c=>c.Key==receipt.Key);
                                var path=VideoDelivery.ReceiptPath(root,clip.Key);
                                try {
                                    if(failure!=null)throw failure;
                                    var row=MatchRow(data,receipt);
                                    if(row==null)throw new InvalidDataException("平台回执未匹配到本段节点。");
                                    string status=Str(row,"status"),run=Str(row,"runId");
                                    if(run.Length>0 && !String.IsNullOrEmpty(receipt.ExternalId) && run!=receipt.ExternalId)
                                        throw new InvalidDataException("平台任务号已变化，请核对画布记录。");
                                    if(run.Length>0)receipt.ExternalId=run;
                                    receipt.Seconds=clip.Seconds;receipt.Error=null;
                                    if(status=="succeeded"){
                                        string temp=DownloadPath(dir,row,receipt);
                                        if(!taken.Add(temp))throw new InvalidDataException("多个节点返回了相同下载文件。");
                                        AgentMedia.ValidateOutput(temp,"视频",clip.Seconds,receipt.Ratio??ratio,
                                            receipt.Resolution??(receipt.ContentKey!=null && receipt.Model!=null || VideoPlatform.IsFixedQuality(receipt.Model)?null:resolution));
                                        receipt.Digest=SystemData.Digest(temp);
                                        receipt.PendingDownload=temp.Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length+1);
                                        receipt.Status="正在重查";VideoDelivery.Save(path,receipt);
                                        if(!FinishDownload(receipt,path))throw new IOException("平台回片临时文件已移动，请重新查询。");
                                        pending.Remove(clip);
                                    }else if(status=="failed"){receipt.Status="平台失败";receipt.Error=RowError(row);pending.Remove(clip);}
                                    else if(status=="idle"){
                                        bool unsubmitted=String.IsNullOrEmpty(receipt.ExternalId);
                                        receipt.Status=unsubmitted?"平台失败":"待核对";
                                        receipt.Error=unsubmitted?"平台确认节点空闲且无任务号，可重新核价。":"节点空闲，但已有平台任务号，请核对原任务。";
                                        pending.Remove(clip);
                                    }
                                    else if(status=="running" || status=="submitted"){receipt.Status=String.IsNullOrEmpty(receipt.ExternalId)?"待核对":"生成中";errors.Remove(clip.Key);}
                                    else throw new InvalidDataException("无法识别的平台任务状态："+status);
                                }catch(Exception e){
                                    if(!PollFailure(receipt,e,errors))pending.Remove(clip);
                                }
                                VideoDelivery.Save(path,receipt);
                                if(FlowCreation.Development)DevMonitor.Log(root,"MEDIA/OCR",clip.Title+" · "+receipt.Status);
                            }
                        }finally{CleanEmptyDirectories(dir);}
                    }
                }
                if(pending.Count>0 && watch.Elapsed<maxWait)token.WaitHandle.WaitOne(watch.Elapsed.TotalMinutes>=15?30000:4000);
            }
            if(pending.Count>0)progress("仍有 "+pending.Count+" 段在即梦平台生成中，已释放项目锁，稍后可点击「查询平台任务状态」继续取回。");
        }
    }
}





