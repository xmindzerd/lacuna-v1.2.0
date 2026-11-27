using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace OneClickFilm
{
    public sealed class ClipReceipt
    {
        public string Provider, Canvas, Node, NodeId, ChargeBatch, PendingDownload;
        public string Key, Title, Status, ExternalId, Output, Error, SubmitError, Utc, Digest;
        public string ContentKey, Model, Resolution, Ratio;
        public int Seconds;
    }

    internal sealed class VideoClip
    {
        internal string Title, Key, Prompt, Scene;
        internal string ContentKey, LegacyContentKey, Model, Resolution, OriginalText, Ratio;
        internal int First, Last;
        internal int Seconds;
        internal List<PromptReference> Images;
    }

    internal static class VideoDelivery
    {
        internal static bool ParseCredits(string text,out decimal credits)
        {
            return decimal.TryParse((text??"").Trim(),System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,out credits) && credits>=0;
        }

        internal static List<FilmSegment> FitGroups(FilmBook book,FilmScene scene,List<FilmSegment> groups,VideoModel model)
        {
            return FitToLimits(book, scene, groups, model, Math.Min(30, model.Max));
        }

        internal static List<FilmSegment> FitToLimits(FilmBook book, FilmScene scene, List<FilmSegment> groups, VideoModel model, int limit)
        {
            int n = scene.Shots.Count;
            var preferred = new HashSet<int>(groups.Select(g => g.Last));
            var next = new int[n];
            var cost = new long[n + 1];
            bool legacy = scene.Shots.All(sh => String.IsNullOrEmpty(sh.code) && !sh.continuation);
            // Work backward so an early cut cannot strand a short tail. Preserve legal
            // narrative cuts, then minimize segment count. Padding is a legacy fallback.
            for (int i = n - 1; i >= 0; i--)
            {
                cost[i] = Int64.MaxValue;
                int seconds = 0, crossed = 0;
                var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int j = i; j < n; j++)
                {
                    if (j > i && preferred.Contains(j - 1)) crossed++;
                    var shot = scene.Shots[j];
                    seconds += shot.seconds;
                    if (seconds > limit) break;
                    foreach (var asset in FilmWriter.Assets(book, shot))
                    {
                        var image = Film.Image(asset, shot.angle);
                        if (image != null) images.Add(image.Digest);
                    }
                    if (images.Count > model.Images) break;
                    if (Film.ProtectedBoundary(scene, j) || cost[j + 1] == Int64.MaxValue) continue;
                    int padding = Math.Max(0, model.Min - seconds);
                    if (padding > 0 && (!legacy || model.Min > limit)) continue;
                    long score = cost[j + 1] + 1 + crossed * (n + 1L) + (preferred.Contains(j) ? 0 : n + 1L) + padding * (n + 1L) * (n + 1L);
                    if (score <= cost[i]) { cost[i] = score; next[i] = j + 1; }
                }
            }
            if (cost[0] == Int64.MaxValue)
                throw new InvalidDataException(scene.Title + " 无法在连续对白、参考图及模型时长限制内分段，请调整分镜。");
            var result = new List<FilmSegment>();
            for (int i = 0; i < n; i = next[i])
            {
                var group = new FilmSegment { First = i, Last = next[i] - 1 };
                // Retain a preferred boundary unless it would leave a mergeable fragment.
                if (result.Count > 0)
                {
                    var prior = result.Last();
                    int left = Film.Shots(scene, prior).Sum(sh => sh.seconds);
                    int right = Film.Shots(scene, group).Sum(sh => sh.seconds);
                    var joined = new FilmSegment { First = prior.First, Last = group.Last };
                    if (Math.Min(left, right) <= 5 && left + right <= limit &&
                        FilmWriter.References(book, scene, joined).Select(r => r.Label).Distinct().Count() <= model.Images)
                    {
                        prior.Last = group.Last;
                        continue;
                    }
                }
                result.Add(group);
            }
            foreach (var group in result)
            {
                int seconds = Film.Shots(scene, group).Sum(sh => sh.seconds);
                if (seconds < model.Min) scene.Shots[group.Last].seconds += model.Min - seconds;
            }
            return result;
        }

        internal const string Folder = @"影片制作\平台视频";
        internal static List<VideoClip> Plan(string root,FilmBook book, VideoOptions options, VideoModel model,Func<FilmScene,FilmSegment,bool> include=null,bool allowEmpty=false)
        {
            var clips=new List<VideoClip>();
            foreach(var scene in book.Scenes.Where(s=>s.Included))
            foreach(var seg in scene.Segments)
            {
                if(include!=null && !include(scene,seg))continue;
                string title="第 "+(book.Scenes.IndexOf(scene)+1)+" 场 · "+scene.Title+" · 第 "+(scene.Segments.IndexOf(seg)+1)+" 段";
                if (!FilmWriter.Current(book,scene,seg)) throw new InvalidDataException(title+" 的提示词需要重新定稿。");
                var accepted=seg.History[seg.Accepted];
                var refs=accepted.References ?? FilmWriter.References(book,scene,seg);
                var images=new List<PromptReference>();
                foreach(var group in refs.GroupBy(r=>r.Label))
                {
                    var r=group.First();
                    if (r.Label!="@Image"+(images.Count+1) || group.Any(other=>other.Digest!=r.Digest))
                        throw new InvalidDataException(title+" 的引用顺序不一致。");
                    if (model!=null)
                    {
                        string refFile = AgentData.Inside(root, r.Path);
                        if (!File.Exists(refFile))
                            throw new InvalidDataException(title+" 缺少引用图片："+r.Name);
                        if (!String.Equals(SystemData.Digest(refFile),r.Digest,StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(title+" 引用图片已更改："+r.Name);
                    }
                    images.Add(r);
                }
                int seconds=Film.Shots(scene,seg).Sum(s=>s.seconds);
                if (model!=null && (seconds<model.Min || seconds>model.Max || images.Count>model.Images))
                    throw new InvalidDataException(title+" 超过所选模型的时长或参考图范围，请调整分段。");
                string body=VideoPromptFidelity.BindForSubmission(accepted,refs);
                var clip=new VideoClip {Scene=scene.Id,First=seg.First,Last=seg.Last,Title=title,Seconds=seconds,
                    Prompt=body,OriginalText=accepted.Text,Images=images,Ratio=book.Brief.Ratio};
                var assets=images.Select(r=>r.Digest).ToArray();
                clip.ContentKey=AgentData.Hash(AssetProduction.StableJson(new {scene.Id,seg.First,seg.Last,Text=body,seconds,book.Brief.Ratio,assets}));
                clip.LegacyContentKey=AgentData.Hash(AssetProduction.StableJson(new {scene.Id,seg.First,seg.Last,Text=accepted.Text,seconds,book.Brief.Ratio,assets}));
                SetChoice(clip,options.Platform,options.Model,options.Resolution);
                clips.Add(clip);
            }
            if(clips.Count==0 && !allowEmpty) throw new InvalidDataException("没有已定稿的片段。");
            var receipts=FindReceipts(root,book,options,clips);
            foreach(var clip in clips)
            {
                ClipReceipt receipt;
                if(receipts.TryGetValue(clip.ContentKey,out receipt)) clip.Key=receipt.Key;
            }
            return clips;
        }

        internal static IEnumerable<string> ReceiptFiles(string root)
        {
            string dir=AgentData.Inside(root,Folder);
            if (!Directory.Exists(dir)) yield break;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                string path=AgentData.Inside(root,Path.Combine(Folder,Path.GetFileName(sub),"任务.json"));
                if(File.Exists(path)) yield return path;
            }
        }

        internal static void RequireAlignedRatio(string root,string ratio)
        {
            if (!FlowCreation.IsFlowProject(root)) return;
            var cfg=FlowCreation.Config(root);
            if (cfg==null || String.IsNullOrWhiteSpace(cfg.Ratio) || cfg.Ratio!=ratio)
                throw new InvalidDataException("一键成片配置画幅（"+(cfg==null?"":cfg.Ratio)+"）与制作记录画幅（"+ratio+"）不一致，请先核对原任务。");
        }

        internal static string MakeAttemptKey(VideoClip clip,string text,string platform,string model,string resolution)
        {
            return AgentData.Hash(AssetProduction.StableJson(new {Id=clip.Scene,clip.First,clip.Last,Text=text,
                seconds=clip.Seconds,clip.Ratio,Platform=platform,Model=model,Resolution=resolution,assets=clip.Images.Select(r=>r.Digest).ToArray()}));
        }

        internal static void SetChoice(VideoClip clip,string platform,string model,string resolution)
        {
            clip.Model=VideoPlatform.Canonical(model);
            clip.Resolution=String.IsNullOrWhiteSpace(resolution)?null:resolution;
            clip.Key=MakeAttemptKey(clip,clip.Prompt,platform,VideoPlatform.Legacy(clip.Model),clip.Resolution);
        }

        internal static Dictionary<string,ClipReceipt> FindReceipts(string root,FilmBook book,VideoOptions defaultOpts,IList<VideoClip> clips)
        {
            var records=new List<ClipReceipt>();
            foreach(var path in ReceiptFiles(root))
            {
                var receipt=AgentData.Read<ClipReceipt>(path);
                var key=Path.GetFileName(Path.GetDirectoryName(path));
                if(receipt==null || !String.Equals(Path.GetFullPath(ReceiptPath(root,key)),path,StringComparison.OrdinalIgnoreCase) ||
                    !String.IsNullOrEmpty(receipt.Key) && !String.Equals(key,receipt.Key,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("平台回执无效，请核对原任务："+key);
                receipt.Key=key;
                records.Add(receipt);
            }
            var legacy=records.Where(r=>String.IsNullOrWhiteSpace(r.ContentKey)).ToDictionary(r=>r.Key,StringComparer.OrdinalIgnoreCase);
            var result=new Dictionary<string,ClipReceipt>();
            var sizes=legacy.Count==0 ? null : VideoPlatform.OfflineCatalog.Models.Concat(VideoPlatform.CurrentCatalog.Models)
                .SelectMany(m=>m.Resolutions).Concat(new[]{null,"","480p","720p","1080p","4k",defaultOpts.Resolution}).Distinct().ToArray();
            foreach(var clip in clips.Where(c=>c.ContentKey!=null))
            {
                var matches=records.Where(r=>r.ContentKey==clip.ContentKey).ToList();
                if(legacy.Count>0)
                {
                    var candidates=from text in new[]{clip.Prompt,clip.OriginalText}.Distinct()
                        from platform in new[]{defaultOpts.Platform,FlowChannels.Dreamina}.Distinct()
                        from model in VideoPlatform.KnownModelIds(defaultOpts.Model,clip.Model)
                         from size in sizes
                        select MakeAttemptKey(clip,text,platform,model,size);
                    foreach(var candidate in candidates)
                    {
                        ClipReceipt receipt;
                        if(legacy.TryGetValue(candidate,out receipt)) matches.Add(receipt);
                    }
                }
                var best=matches.OrderByDescending(r=>r.Status=="已取回"?2:r.Status=="平台失败"?0:1)
                    .ThenByDescending(r=>r.Utc,StringComparer.Ordinal).FirstOrDefault();
                if(best!=null)result.Add(clip.ContentKey,best);
            }
            return result;
        }

        internal static List<VideoClip> PlanForSelections(string root,FilmBook book,VideoOptions defaults,
            IDictionary<string,VideoClipChoice> choices,VideoPlatform catalog=null)
        {
            var clips=Plan(root,book,defaults,null,(s,g)=>FilmWriter.Current(book,s,g),true)
                .Where(c=>choices.ContainsKey(c.ContentKey)).ToList();
            if(clips.Count==0 || clips.Count!=choices.Count)throw new InvalidDataException("所选片段或定稿已变化，请重新选段。");
            foreach(var clip in clips)
            {
                if(NovelVideo.Read(root,clip.Key)!=null)throw new IOException(clip.Title+" 已有平台任务，请核对原任务。");
                var choice=choices[clip.ContentKey];
                SetChoice(clip,defaults.Platform,choice.Model,choice.Resolution);
                NovelVideo.ValidateClip(catalog??VideoPlatform.CurrentCatalog,clip,book.Brief.Ratio);
            }
            return clips;
        }

        internal static string ReceiptPath(string root,string key)
        {
            if(!Regex.IsMatch(key??"", @"\A[0-9A-Fa-f]{64}\z")) throw new InvalidDataException("片段标识无效。");
            return AgentData.Inside(root,Path.Combine(Folder,key,"任务.json"));
        }

        internal static void Save(string path,ClipReceipt receipt)
        {
            receipt.Utc=AnalysisData.Now();
            AgentData.Save(path,receipt);
        }

        // The flow's exclusive project lease spans the whole submit / query / save sequence.
        internal static void RecoverLegacy(string root,VideoPlatform platform, VideoOptions options,string ratio,
            List<VideoClip> clips, Action<string> report,CancellationToken token,Action checkpoint)
        {
            if(String.IsNullOrWhiteSpace(platform.Exe))throw new IOException("此任务由旧版即梦 CLI 提交，需要该 CLI 查询原任务。");
            FlowChannels.CheckDreamina(platform.Exe,token);
            foreach(var clip in clips)
            {
                checkpoint();
                string path=ReceiptPath(root,clip.Key);
                string dir=Path.GetDirectoryName(path);
                var receipt=AgentData.Read<ClipReceipt>(path);
                if(receipt!=null && receipt.Status=="已取回" && !String.IsNullOrEmpty(receipt.Output))
                {
                    var saved=AgentData.Inside(root,receipt.Output);
                    if(File.Exists(saved) && SystemData.Digest(saved)==receipt.Digest) continue;
                    receipt.Status="待取回"; Save(path,receipt);
                }
                if(receipt==null || receipt.Provider=="novel")throw new IOException("此片段没有旧版提交记录。");
                if(!ValidId(receipt.ExternalId) || receipt.Status=="平台失败")
                    throw new InvalidOperationException(clip.Title+" · 请在视频生成页核对原任务。 "+receipt.Error);
                string download=Directory.CreateDirectory(Path.Combine(dir,"回片")).FullName;
                var timer=Stopwatch.StartNew();
                while(timer.Elapsed < TimeSpan.FromMinutes(15))
                {
                    checkpoint(); report(clip.Title+" · 查询即梦任务 "+receipt.ExternalId);
                    var result=CodexConnection.Run(platform.Exe,AgentRunner.Args(new[]{"query_result","--submit_id",receipt.ExternalId,"--download_dir",download}),
                        90000,dir,cancellation:token,useSystemProxy:true);
                    string status=FlowChannels.Field(result.Output,"gen_status");
                    if(new[]{"fail","failed","cancelled","canceled"}.Contains(status,StringComparer.OrdinalIgnoreCase))
                    {
                        receipt.Status="平台失败";receipt.Error=AgentData.Short(result.Output,500);Save(path,receipt);
                        throw new InvalidOperationException(clip.Title+" · 平台报告失败，请在视频生成页核对。");
                    }
                    var files=Directory.GetFiles(download,"*.mp4",SearchOption.AllDirectories);
                    if(result.Code==0 && files.Length==1)
                    {
                        var relative=files[0].Substring(Path.GetFullPath(root).TrimEnd('\\','/').Length+1);
                        string file=AgentData.Inside(root,relative);
                        AgentMedia.ValidateOutput(file,"视频",clip.Seconds,receipt.Ratio??ratio,receipt.Resolution??options.Resolution);
                        receipt.Output=relative;receipt.Status="已取回";receipt.Digest=SystemData.Digest(file);Save(path,receipt);
                        break;
                    }
                    if(files.Length>1)throw new InvalidDataException(clip.Title+" 返回了多个视频，请检查回片目录。");
                    if(result.Code!=0)throw new IOException(clip.Title+" 查询失败，已保留平台任务号。 "+AgentData.Short(result.Error,180));
                    token.WaitHandle.WaitOne(4000);
                }
                if(receipt.Status!="已取回")throw new TimeoutException(clip.Title+" 仍在平台处理中，继续制作会查询同一任务号。");
            }
        }

        internal static bool ValidId(string id){return Regex.IsMatch(id??"", @"\A[A-Za-z0-9_-]{1,160}\z");}
    }
}

