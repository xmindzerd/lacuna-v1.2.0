using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Diagnostics;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace OneClickFilm
{
    public sealed class VideoOptions
    {
        public string Platform = "prompt", Model, Resolution;
        public bool Review;
        internal bool Enabled { get { return Platform == FlowChannels.Dreamina; } }
    }

    internal sealed class VideoModel
    {
        internal string Name, Title, DefaultResolution, Price;
        internal string[] Ratios;
        public override string ToString() { return String.IsNullOrEmpty(Title) || Name==Title ? Name : Title+" · "+Name; }
        internal string[] Resolutions;
        internal int Min, Max, Images;
    }

    internal sealed class VideoPlatform
    {
        internal string Exe;
        internal string[] Ratios;
        internal List<VideoModel> Models = new List<VideoModel>();

        internal bool Offline;
        internal string LoadError;
        internal static VideoPlatform OfflineCatalog { get { return offlineModels.Value; } }
        internal static VideoPlatform CurrentCatalog { get { return cached??OfflineCatalog; } }
        internal static string[] KnownModelIds(params string[] extra)
        {
            var models=OfflineCatalog.Models.Concat((cached??OfflineCatalog).Models);
            return models.Select(m=>m.Name).Concat(aliases.Keys).Concat(aliases.Values).Concat(extra).Distinct().ToArray();
        }
        private static readonly SemaphoreSlim catalogLock = new SemaphoreSlim(1);
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long expires;
        private static volatile VideoPlatform cached;
        private static readonly Lazy<VideoPlatform> offlineModels=new Lazy<VideoPlatform>(NovelCatalog);
        private static string cachedCli;
        private static readonly Dictionary<string,string> aliases = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"seedance2.5","Seedance_2.5"},{"seedance2.0_vip","seedance2.0_vision"},
            {"seedance2.0fast_vip","seedance2.0_fast_vision"},{"seedance2.0mini","Seedance_2.0_mini"}
        };
        internal static string Canonical(string id)
        {
            string value;
            return id!=null && aliases.TryGetValue(id,out value) ? value : id;
        }
        internal static string Legacy(string id)
        {
            return aliases.FirstOrDefault(a=>a.Value==id).Key ?? id;
        }

        internal static bool IsFixedQuality(string id)
        {
            var model=(cached??OfflineCatalog).Models.Concat(OfflineCatalog.Models).FirstOrDefault(m=>Canonical(m.Name)==Canonical(id));
            return model!=null && model.Resolutions.Length==0;
        }

        internal static string DisplayName(string id)
        {
            var model=(cached??offlineModels.Value).Models.FirstOrDefault(m=>Canonical(m.Name)==Canonical(id));
            return model==null || String.IsNullOrEmpty(model.Title)?id:model.Title;
        }

        private static VideoPlatform NovelCatalog()
        {
            using(var stream=typeof(VideoPlatform).Assembly.GetManifestResourceStream("VideoModels.json"))
            using(var reader=new StreamReader(stream))
                return Catalog(AgentData.Json().Deserialize<Map[]>(reader.ReadToEnd()),true);
        }
        private static string[] Strings(Map row,string field)
        {
            var items=AgentData.Value(row,field) as IList;
            return items==null ? new string[0] : items.OfType<string>().Distinct().ToArray();
        }
        private static VideoPlatform Catalog(IEnumerable<Map> rows,bool offline)
        {
            var result=new VideoPlatform{Offline=offline};
            foreach(var row in rows)
            {
                var duration=CodexAppServer.Child(row,"durations");
                var cost=CodexAppServer.Child(row,"priceHint");
                var priceResolution=CodexAppServer.S(cost,"resolution");
                var model=new VideoModel {
                    Name=CodexAppServer.S(row,"modelId"), Title=CodexAppServer.S(row,"name"), Ratios=Strings(row,"ratios"),
                    Resolutions=Strings(row,"resolutions"), DefaultResolution=CodexAppServer.S(row,"defaultResolution"),
                    Min=(int)AgentData.Number(duration,"min"), Max=(int)AgentData.Number(duration,"max"),
                    Images=(int)AgentData.Number(CodexAppServer.Child(row,"referenceLimits"),"image"),
                    Price=cost==null?"":"参考 "+CodexAppServer.S(cost,"credits")+" 积分 / "+CodexAppServer.S(cost,"billedAmount")+
                        (CodexAppServer.S(cost,"billedUnit")=="second"?" 秒":" "+CodexAppServer.S(cost,"billedUnit"))+(String.IsNullOrWhiteSpace(priceResolution)?"":" "+priceResolution)
                };
                if(model.Name.Length==0 || model.Min<=0 || model.Max<model.Min || model.Ratios.Length==0)
                    throw new InvalidDataException("即梦视频模型能力不完整。");
                result.Models.Add(model);
            }
            if(result.Models.Count==0)throw new InvalidDataException("即梦视频模型目录为空。");
            result.Ratios=result.Models.SelectMany(m=>m.Ratios).Distinct().ToArray();
            return result;
        }
        private static Map ReadModel(string cli,CancellationToken token,params string[] args)
        {
            var response=CodexConnection.Run(cli,AgentRunner.Args(args.Concat(new[]{"-j","--full"})),18000,cancellation:token,useSystemProxy:true);
            if(response.Code!=0)throw new IOException("即梦模型目录暂不可用。");
            var msg=AgentData.Json().Deserialize<Map>(response.Output);
            var data=CodexAppServer.Child(msg,"data");
            if(data==null || CodexAppServer.S(msg,"status")!="success")throw new InvalidDataException("即梦模型目录响应无效。");
            return data;
        }
        internal static void Invalidate(){Interlocked.Exchange(ref expires,0);}

        internal static VideoPlatform Load(CancellationToken token)
        {
            var novel=NovelVideo.Find();
            catalogLock.Wait(token);
            try {
                if(cached!=null && cachedCli==novel && clock.ElapsedMilliseconds<Interlocked.Read(ref expires))return cached;
                VideoPlatform result;
                var legacy=FlowChannels.InstalledDreamina();
                try {
                    if(novel!=null){
                        if(!NovelAuth.Connected(novel,token))throw new IOException("即梦账号尚未授权。");
                        var list=ReadModel(novel,token,"model","list","--type","video");
                        var ids=CodexAppServer.Items(list,"models").Select(r=>CodexAppServer.S(r,"modelId")).Distinct().ToArray();
                        var rows=new Map[ids.Length];
                        Parallel.For(0,ids.Length,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=token},i=>{
                            rows[i]=ReadModel(novel,token,"model","describe",ids[i]);
                            if(CodexAppServer.S(rows[i],"modelId")!=ids[i])throw new InvalidDataException("即梦模型编号不匹配。");
                        });
                        result=Catalog(rows,false);
                    } else if(legacy!=null){
                        var response=CodexConnection.Run(legacy.Executable,"multimodal2video -h",12000,cancellation:token);
                        result=Parse(response.Output);
                    } else { result=NovelCatalog(); result.LoadError="本机未找到即梦 CLI。"; }
                } catch(Exception error) {
                    token.ThrowIfCancellationRequested();
                    var reason=error.GetBaseException();
                    if(!(reason is IOException || reason is InvalidDataException || reason is TimeoutException ||
                        reason is InvalidOperationException || reason is ArgumentException || reason is UnauthorizedAccessException ||
                        reason is System.ComponentModel.Win32Exception || error is AggregateException))throw;
                    result=NovelCatalog(); result.LoadError=CodexAuthMode.RedactDiagnostic(reason.Message);
                }
                result.Exe=legacy==null?null:legacy.Executable;
                cachedCli=novel; cached=result; Interlocked.Exchange(ref expires,clock.ElapsedMilliseconds+(result.Offline?10000:300000));
                return result;
            } finally { catalogLock.Release(); }
        }

        internal static VideoPlatform Parse(string help)
        {
            var catalog = new VideoPlatform();
            var names = Regex.Match(help, @"--model_version\s+string\s+supported values:\s*([A-Za-z0-9_., ]+)");
            var ratio = Regex.Match(help, @"--ratio\s+string\s+supported values:\s*([\d:, ]+)");
            catalog.Ratios = Regex.Matches(ratio.Groups[1].Value, @"\d+:\d+").Cast<Match>().Select(m=>m.Value).ToArray();
            var limits = Regex.Match(help, @"--duration\s+int\s+[^\r\n]*?by model:\s*([^\r\n(]+)").Groups[1].Value;
            var sizes = Regex.Match(help, @"--video_resolution\s+string\s+[^\r\n]*?by model:\s*([^\r\n]+)").Groups[1].Value;
            foreach (string model in names.Groups[1].Value.Split(',').Select(n=>n.Trim()).Where(n=>n.Length>0))
            {
                string escaped = Regex.Escape(model);
                bool family = model.StartsWith("seedance2.0",StringComparison.Ordinal);
                string imageRule = @"(?m)^- " + (family ? @"seedance2\.0 family[^\r\n]*?" : escaped) + @" -> [^\r\n]*?image<=(\d+)";
                var images = Regex.Match(help,imageRule);
                var duration = Rule(limits,escaped,family,@"(\d+)-(\d+)");
                var resolution = Rule(sizes,escaped,family,@"([^;\r\n]+)");
                if (!duration.Success || !resolution.Success || !images.Success) continue;
                var opts = Regex.Matches(resolution.Groups[1].Value,@"\b\d+[pk]\b").Cast<Match>().Select(m=>m.Value).ToArray();
                if (opts.Length>0) catalog.Models.Add(new VideoModel { Name=model, Ratios=catalog.Ratios, Min=int.Parse(duration.Groups[1].Value),
                    Max=Math.Min(30,int.Parse(duration.Groups[2].Value)), Images=int.Parse(images.Groups[1].Value), Resolutions=opts });
            }
            if (catalog.Models.Count==0 || catalog.Ratios.Length==0) throw new InvalidDataException("当前即梦 CLI 帮助格式无法识别，请检查版本和视频参数。");
            return catalog;
        }

        static Match Rule(string rules,string model,bool knownFamily,string value)
        {
            var match=Regex.Match(rules,@"(?:^|;\s*)"+model+@" -> "+value);
            return !match.Success && knownFamily ? Regex.Match(rules,@"(?:^|;\s*)all other (?:seedance2\.0 )?models -> "+value) : match;
        }

        internal VideoModel Require(VideoOptions choices,string ratio)
        {
            var model=Models.SingleOrDefault(m=>Canonical(m.Name)==Canonical(choices.Model));
            if(model==null)throw new InvalidDataException("模型目录中没有 "+choices.Model+"。");
            if(!model.Ratios.Contains(ratio))throw new InvalidDataException(model.Name+" 不支持画幅 "+ratio+"。");
            if(model.Resolutions.Length==0 ? !String.IsNullOrWhiteSpace(choices.Resolution) : !model.Resolutions.Contains(choices.Resolution))
                throw new InvalidDataException(model.Name+" 不支持分辨率 "+choices.Resolution+"。");
            return model;
        }

        internal static VideoModel ProjectModel(string root,string ratio)
        {
            var cfg=FlowCreation.Config(root);
            return cfg==null ? null : For(cfg.Video,ratio,CancellationToken.None);
        }

        internal static VideoModel For(VideoOptions choices,string ratio,CancellationToken token)
        {
            if (choices==null || choices.Platform=="prompt") return null;
            if (!choices.Enabled) throw new InvalidDataException("视频平台暂不可用。");
            return Load(token).Require(choices,ratio);
        }
    }
}
