using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OneClickFilm
{
    internal sealed partial class Workspace
    {
        private void ShowPromptMonitor(Action back, string restoreScene=null, int restoreShot=-1)
        {
            string root = projectFolder;
            BeginPage(1, Stage.All[4].Name, DisplayName(), 5);
            var book = Film.Open(root);
            if(book.RatioMismatchNote!=null)content.Children.Add(Notice(book.RatioMismatchNote));
            var scenes = book.Scenes.Where(s => s.Included && s.Segments.Count > 0).ToList();
            if (back != null) AddPageBack("返回", back);
            var tabs = new WrapPanel();
            AddPromptTabs(tabs,true,restoreScene,back);
            content.Children.Add(tabs);
            if (scenes.Count == 0)
            {
                content.Children.Add(Notice("分段完成后可在这里查阅提示词。"));
                if (FlowCreation.IsFlowProject(root))
                return;
            }
            var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 12) };
            var sceneList = new ComboBox { MinWidth = 220, MaxWidth = 480, ToolTip = "选择场次" };
            var segmentList = new ComboBox { MinWidth = 170, Margin = new Thickness(8,0,0,0), ToolTip = "选择片段" };
            bar.Children.Add(sceneList); bar.Children.Add(segmentList); content.Children.Add(bar);
            PromptReviewBar(root,content);
            var panel = new StackPanel { Margin = new Thickness(14) };
            content.Children.Add(Panel(panel));
            foreach (var scene in scenes) sceneList.Items.Add(scene.Title);
            segmentList.SelectionChanged += delegate {
                if(quoteWork!=null){quoteWork.Cancel();quoteWork=null;}
                panel.Children.Clear();
                bar.BringIntoView();
                if (sceneList.SelectedIndex < 0 || segmentList.SelectedIndex < 0) return;
                var scene = scenes[sceneList.SelectedIndex];
                var seg = scene.Segments[segmentList.SelectedIndex];
                var shots = Film.Shots(scene,seg).ToList();
                var accepted = seg.Accepted >= 0 && seg.Accepted < seg.History.Count ? seg.History[seg.Accepted] : null;
                panel.Children.Add(Title("第 " + (segmentList.SelectedIndex+1) + " 段　·　第 " + (seg.First+1) + "–" + (seg.Last+1) + " 镜",20));
                if (accepted == null) { panel.Children.Add(Notice("本段尚无已采纳的最终提示词。")); return; }
                var issues = FilmWriter.PromptIssues(book,scene,seg,accepted);
                string status = issues.Length > 0 ? "待核对 · " + String.Join("、",issues) :
                    FilmWriter.Current(book,scene,seg) ? "对白与素材引用校验通过" : "上游内容已更新，请核对本稿";
                panel.Children.Add(StudioText(shots.Sum(sh=>sh.seconds) + " 秒　·　" + book.Brief.Ratio + "　·　" + status,14,4));
                if (accepted.SkillFaithful)
                    panel.Children.Add(Muted("生成 Skill · " + accepted.Skill + " · " + accepted.SkillDigest, 13, 4));
                else if (!String.IsNullOrWhiteSpace(accepted.Skill))
                    panel.Children.Add(Muted("历史稿来源 · " + accepted.Skill + "（未记录 Skill 指纹）", 13, 4));
                var cues = shots.SelectMany(sh=>Film.Covered(scene,sh)).Distinct().ToList();
                var suspect = cues.Where(c => PdfScript.SuspectEnglish(c.Text)).ToList();
                if (suspect.Count != 0) panel.Children.Add(Notice("请对照原页检查英文断词或大小写 · 原文行 " + String.Join("、",suspect.Select(c=>c.First).Distinct())));
                var refs = accepted.References;
                if (refs == null && FilmWriter.Matches(book,scene,seg,accepted.Signature))
                    refs = FilmWriter.References(book,scene,seg);
                var destinations=new System.Collections.Generic.Dictionary<string,FrameworkElement>();
                string displayText=accepted.Text;
                try { displayText=VideoPromptFidelity.DisplayText(accepted,refs ?? new System.Collections.Generic.List<PromptReference>()); }
                catch (System.IO.InvalidDataException issue) { panel.Children.Add(Notice(issue.Message)); }
                PresentPrompt(panel,root,accepted,displayText,refs,destinations);
                var actions=new WrapPanel();
                var videoCfg=FlowCreation.Config(root);
                if(videoCfg!=null && videoCfg.Video!=null && videoCfg.Video.Enabled && issues.Length==0 && FilmWriter.Current(book,scene,seg)){
                    try {
                        var clip=VideoDelivery.Plan(root,book,videoCfg.Video,null,(sc,g)=>sc==scene && g==seg,true).FirstOrDefault();
                        if(clip!=null){
                            var r=NovelVideo.Read(root,clip.Key);
                            if(r==null)
                            {
                                string segmentKey=SegmentChoiceKey(clip);
                                Action<bool> price;
                                var quotePanel=QuotePanel(root,videoCfg,book,()=>new System.Collections.Generic.List<VideoClip>{clip},
                                    c=>PickerChoice(segmentKey,videoCfg.Video),out price);
                                var generate=ActionButton(videoCfg.Video.Review?"审核并生成本段":"生成本段",()=>price(true),true);
                                generate.IsEnabled=book.RatioMismatchNote==null && !AssetRunning(root,VideoLane) && !AssetRunning(root,FlowTaskName);
                                actions.Children.Add(VideoPicker(root,videoCfg.Video,segmentKey,generate,()=>{if(generate.IsEnabled)price(false);}));
                                if(generate.IsEnabled){panel.Children.Add(quotePanel);price(false);}
                            }
                            else if(NovelVideo.Returned(root,r))actions.Children.Add(ActionButton("监看本段视频",()=>PlayClip(root,r),true));
                            else actions.Children.Add(ActionButton("查看本段生成任务",ShowVideoJobs,false));
                            if(r!=null)panel.Children.Add(Muted(ReceiptChoice(r),13,4));
                        }
                    } catch (Exception planErr) {
                        panel.Children.Add(Notice("视频生成状态暂不可用 · " + AgentData.Short(planErr.Message, 240)));
                    }
                }
                actions.Children.Add(ActionButton("复制本段提示词",()=>CopyText(displayText,"已复制本段提示词"),false));
                if(cues.Count>0) actions.Children.Add(ActionButton("对照剧本原页",()=>ShowScriptReader("返回提示词监看台",()=>ShowPromptMonitor(back,scene.Id,seg.First),cues[0].First,AnalysisData.CurrentScript(root)),false));
                panel.Children.Add(actions);
                panel.Children.Add(Title("本段引用的资产图",18));
                if (refs == null)
                {
                    panel.Children.Add(Notice("这份历史稿没有保存引用快照，上游已变化。请按旧导出的交接清单核对。"));
                    return;
                }
                if (refs.Count == 0) panel.Children.Add(StudioText("本段未引用资产图。",14,4));
                else panel.Children.Add(StudioText(String.Join("　", refs.Select(r=>r.Label + " " + r.Name)),14,4));
                var cards = new WrapPanel();
                foreach (var reference in refs)
                {
                    var card = new StackPanel { Width = 280, Margin = new Thickness(0,8,12,6) };
                    card.Children.Add(StudioText(reference.Caption,15,2));
                    var asset = book.Assets.FirstOrDefault(a=>a.Name == reference.Name && a.Images.Any(im=>im.Digest == reference.Digest));
                    if (asset != null && !String.IsNullOrWhiteSpace(asset.Look) && asset.Evidence != null && asset.Evidence.Count > 0 &&
                        !asset.Evidence.Any(n=>n >= scene.First && n <= scene.Last))
                        card.Children.Add(Notice("本场未列在该造型的剧本依据中，请核对复用。 " + asset.Scope));
                    if (asset != null && !String.IsNullOrEmpty(asset.Panels) && Film.Image(asset,"主图") != null && Film.Image(asset,"主图").Digest == reference.Digest)
                    {
                        card.Children.Add(StudioText("参考图分格 · " + asset.Panels,13,4));
                        if (!Regex.IsMatch(accepted.Text,@"[左右上下]格|第[一二三四五六七八九\d]+格"))
                            card.Children.Add(Notice("本段未注明采用哪一格。"));
                    }
                    AddAssetPreview(card,root,new AssetAttempt { Path = reference.Path, Digest = reference.Digest });
                    if(!destinations.ContainsKey(reference.Label)) destinations.Add(reference.Label,card);
                    cards.Children.Add(card);
                }
                panel.Children.Add(cards);
            };
            sceneList.SelectionChanged += delegate {
                segmentList.Items.Clear();
                if(sceneList.SelectedIndex<0)return;
                var scene=scenes[sceneList.SelectedIndex];
                for(int i=0;i<scene.Segments.Count;i++){
                    var seg=scene.Segments[i];
                    segmentList.Items.Add("第 " + (i+1) + " 段 · " + Film.Shots(scene,seg).Sum(sh=>sh.seconds) + " 秒");
                }
                segmentList.SelectedIndex=0;
            };
            int index=scenes.FindIndex(s=>s.Id==restoreScene);
            if(index < 0) index=scenes.FindIndex(s=>s.Segments.Any(g=>g.Accepted>=0 && g.Accepted<g.History.Count));
            sceneList.SelectedIndex=Math.Max(0,index);
            if(index>=0) segmentList.SelectedIndex=Math.Max(0,scenes[index].Segments.FindIndex(g=>
                restoreShot>=0 ? g.First==restoreShot : g.Accepted>=0 && g.Accepted<g.History.Count));
        }
    }
}
