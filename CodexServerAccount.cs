using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace OneClickFilm
{
    internal sealed partial class CodexAppServer
    {
        private static volatile CodexAppServer displayed;
        private volatile AgentModel[] liveModels=new AgentModel[0];
        private volatile string shortQuota="5h 未提供 · 周未提供",fullQuota="5 小时额度未提供 · 周额度未提供";
        private volatile string defaultModel="", configuredModel="";
        internal static AgentModel[] LiveModels { get {var server=displayed;return server==null?new AgentModel[0]:server.liveModels;} }
        internal static string CompactQuota {get{if(CodexAuthMode.IsApi)return "API 直连";var s=displayed;return s==null?"额度未知":s.shortQuota;}}
        internal static string Summary(AgentOptions options)
        {
            if(CodexAuthMode.IsApi)return "OpenAI Responses API 直连 · 模型 "+CodexAuthMode.Model+" · "+CodexAuthMode.BaseUrl;
            var s=displayed;
            string key=(options==null?"":options.Model) ?? "";
            if(key.Length==0 && s!=null)key=s.configuredModel;
            if(String.IsNullOrEmpty(key) && s!=null)key=s.defaultModel;
            var model=LiveModels.FirstOrDefault(x=>x.Key==key);
            string effort=options==null?null:options.ReasoningEffort;
            if(model!=null && !model.Efforts.Contains(effort))effort=model.DefaultEffort;
            return "模型 "+(String.IsNullOrEmpty(key)?"Codex 默认":key)+" · 推理 "+(String.IsNullOrEmpty(effort)?"模型默认":effort)
                +"\n"+(s==null?"5 小时额度未知 · 周额度未知":s.fullQuota);
        }
        // Updated only on the stdout reader, in wire order (including full read responses).
        private readonly Dictionary<long,Map> windows=new Dictionary<long,Map>();
        private readonly Dictionary<string,long> slotDurations=new Dictionary<string,long>();
        private string limitGroup="codex";
        private void UpdateQuota(Map data,bool full)
        {
            if(full)
            {
                var quota=Child(Child(data,"rateLimitsByLimitId"),"codex") ?? Child(data,"rateLimits");
                string group=S(quota,"limitId");
                if(group.Length==0)group="codex";
                if(group!=limitGroup)slotDurations.Clear();
                limitGroup=group;
                data=quota;
            }
            else
            {
                string group=S(data,"limitId");
                if((group.Length==0?"codex":group)!=limitGroup)return;
            }
            var previous=new Dictionary<string,long>(slotDurations);
            if(full){windows.Clear();slotDurations.Clear();}
            // Explicit durations take precedence when slots move between primary and secondary.
            var resolved=new HashSet<long>();
            foreach(string name in new[]{"primary","secondary"})
            {
                var w=Child(data,name);
                if(w==null || AgentData.Value(w,"windowDurationMins")==null)continue;
                long duration=AgentData.Number(w,"windowDurationMins");
                foreach(var old in slotDurations.Where(x=>x.Value==duration).Select(x=>x.Key).ToArray())slotDurations.Remove(old);
                slotDurations[name]=duration;
                resolved.Add(duration);
                if(AgentData.Value(w,"usedPercent")!=null)SetWindow(duration,w);
            }
            foreach(string name in new[]{"primary","secondary"})
            {
                var w=Child(data,name);
                long duration;
                if(w==null || AgentData.Value(w,"usedPercent")==null || AgentData.Value(w,"windowDurationMins")!=null ||
                    !previous.TryGetValue(name,out duration) || !resolved.Add(duration))continue;
                slotDurations[name]=duration;
                SetWindow(duration,w);
            }
            string shortA="5h 未提供",shortB="周未提供",a="5 小时额度未提供",b="周额度未提供";
            foreach(long mins in new long[]{300,10080})
            {
                Map w;
                if(!windows.TryGetValue(mins,out w))continue;
                long percent=100-Math.Max(0,Math.Min(100,AgentData.Number(w,"usedPercent")));
                long stamp=AgentData.Number(w,"resetsAt");
                string reset=stamp>0 && stamp<253402214400L?new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)
                    .AddSeconds(stamp).ToLocalTime().ToString("MM-dd HH:mm",CultureInfo.InvariantCulture)+" 重置":"重置时间未提供";
                string detail="剩余 "+percent+"% · "+reset+(percent<=10?" · 额度偏低":"");
                if(mins==300){shortA="5h "+percent+"%";a="5 小时"+detail;}
                else {shortB="周 "+percent+"%";b="每周"+detail;}
            }
            shortQuota=shortA+" · "+shortB;fullQuota=a+"  /  "+b;
        }
        private void SetWindow(long duration,Map update)
        {
            if(duration!=300 && duration!=10080)return;
            Map previous;
            var value=windows.TryGetValue(duration,out previous)?new Map(previous):new Map();
            foreach(var pair in update)value[pair.Key]=pair.Value;
            windows[duration]=value;
        }
        internal static void RefreshAccount(string exe)
        {
            if(CodexAuthMode.IsApi)return;
            CodexAppServer s;
            try{s=Open(exe);}
            catch(Rejected){s=null;}
            displayed=s;
            if(s==null)return;
            s.configuredModel=AgentRunner.ConfiguredModel();
            try{s.Call("account/rateLimits/read",new {},10000);}
            catch(IOException){} catch(TimeoutException){}
            try
            {
                var catalog=new List<AgentModel>();
                string next=null;
                var seen=new HashSet<string>();
                do
                {
                    var data=s.Call("model/list",new {cursor=next,limit=100},10000);
                    foreach(var row in Items(data,"data").Where(r=>!Object.Equals(AgentData.Value(r,"hidden"),true)))
                    {
                        string key=S(row,"model");
                        if(String.IsNullOrEmpty(key))continue;
                        catalog.Add(new AgentModel{Key=key,Name=S(row,"displayName"),DefaultEffort=S(row,"defaultReasoningEffort"),
                            Efforts=Items(row,"supportedReasoningEfforts").Select(x=>S(x,"reasoningEffort")).ToArray()});
                        if(Object.Equals(AgentData.Value(row,"isDefault"),true))s.defaultModel=key;
                    }
                    next=S(data,"nextCursor");
                }while(next.Length!=0 && seen.Add(next));
                s.liveModels=catalog.ToArray();
            }catch(IOException){} catch(TimeoutException){}
        }
    }
}