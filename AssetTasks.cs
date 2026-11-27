using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneClickFilm
{
    // Each operation shares the script/version guard and owns its own writable resource.
    internal sealed class ProjectTaskLease : IDisposable
    {
        private readonly FileStream projectGuard, resourceGuard;
        internal ProjectTaskLease(string root, string resource)
        {
            string folder = AgentData.DirectoryFor(root);
            Directory.CreateDirectory(folder);
            projectGuard = File.Open(Path.Combine(folder, "运行.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
            try
            {
                resourceGuard = new FileStream(Path.Combine(folder, "任务-" + AgentData.Hash(resource) + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch { projectGuard.Dispose(); throw; }
        }
        public void Dispose() { resourceGuard.Dispose(); projectGuard.Dispose(); }
    }

    internal sealed class PromptWriteLeases : IDisposable
    {
        private readonly List<ProjectTaskLease> held = new List<ProjectTaskLease>();
        internal PromptWriteLeases(string root, IEnumerable<string> ids)
        {
            try { foreach (var id in ids.Distinct().OrderBy(x => x, StringComparer.Ordinal)) held.Add(new ProjectTaskLease(root, "编辑提示词 · " + id)); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var lease in held) lease.Dispose(); held.Clear(); }
    }

    internal sealed class ImageWriteLeases : IDisposable
    {
        private readonly List<ProjectTaskLease> held = new List<ProjectTaskLease>();
        internal void Claim(string root, IEnumerable<string> resources)
        {
            try
            {
                foreach (var resource in resources.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                    held.Add(new ProjectTaskLease(root, resource));
            }
            catch (IOException e)
            {
                Dispose();
                throw new IOException("所选资产或共用基础图已有任务，请查看该项进度。", e);
            }
        }
        public void Dispose() { foreach (var lease in held) lease.Dispose(); held.Clear(); }
    }

    internal static partial class AssetProduction
    {
        private static void SaveAssetFile(string path, AssetWork work)
        {
            var json = AgentData.Json();
            string own = json.Serialize(work);
            using (var mutex = new Mutex(false, "OneClickFilm.Asset." + AgentData.Hash(Path.GetFullPath(path).ToUpperInvariant())))
            {
                try { if (!mutex.WaitOne(10000)) throw new IOException("制作记录正在写入，请稍后重试。"); }
                catch (AbandonedMutexException) { }
                try
                {
                    var current = AgentData.Read<AssetWork>(path);
                    if (current == null || work.SavedSnapshot == null)
                    {
                        if (current != null && !SameValue(json.DeserializeObject(json.Serialize(current)), json.DeserializeObject(own))) throw new IOException("制作记录已存在，请重新打开。");
                        AgentData.Save(path, work);
                    }
                    else
                    {
                        var before = (Dictionary<string, object>)json.DeserializeObject(work.SavedSnapshot);
                        var local = (Dictionary<string, object>)json.DeserializeObject(own);
                        var disk = (Dictionary<string, object>)json.DeserializeObject(json.Serialize(current));
                        var a = (object[])before["Items"]; var b = (object[])local["Items"]; var c = (object[])disk["Items"];
                        if (a.Length != b.Length || b.Length != c.Length) throw new IOException("资产清单已变化，请重新打开。");
                        for (int i = 0; i < a.Length; i++)
                        {
                            var oldItem = (Dictionary<string, object>)a[i];
                            var newItem = (Dictionary<string, object>)b[i];
                            var savedItem = (Dictionary<string, object>)c[i];
                            if (!Equals(oldItem["Id"], newItem["Id"]) || !Equals(oldItem["Id"], savedItem["Id"]))
                                throw new IOException("资产编号已变化，请重新打开。");
                            MergeFields(oldItem, newItem, savedItem);
                        }
                        before.Remove("Items"); local.Remove("Items");
                        MergeMap("OtherImageBatches", before, local, disk);
                        MergeMap("BaseImages", before, local, disk);
                        MergeMap("BaseHistory", before, local, disk);
                        MergeFields(before, local, disk);
                        AgentData.Save(path, disk);
                    }
                    work.SavedSnapshot = own;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void MergeMap(string field, Dictionary<string, object> before, Dictionary<string, object> local, Dictionary<string, object> disk)
        {
            var oldMap = before[field] as Dictionary<string, object> ?? new Dictionary<string, object>();
            var newMap = local[field] as Dictionary<string, object> ?? new Dictionary<string, object>();
            var savedMap = disk[field] as Dictionary<string, object> ?? new Dictionary<string, object>();
            foreach (var key in oldMap.Keys.Union(newMap.Keys))
            {
                object oldValue, newValue, savedValue;
                oldMap.TryGetValue(key, out oldValue); newMap.TryGetValue(key, out newValue); savedMap.TryGetValue(key, out savedValue);
                if (SameValue(oldValue, newValue)) continue;
                if (!SameValue(savedValue, oldValue) && !SameValue(savedValue, newValue))
                    throw new IOException("该批次或基础图已被其他任务更新，请重新打开。");
                if (newValue == null) savedMap.Remove(key); else savedMap[key] = newValue;
                disk[field] = savedMap;
            }
            before.Remove(field); local.Remove(field);
        }

        private static void MergeFields(Dictionary<string, object> before, Dictionary<string, object> local, Dictionary<string, object> disk)
        {
            foreach (var pair in local)
            {
                object beforeVal, diskVal;
                before.TryGetValue(pair.Key, out beforeVal);
                disk.TryGetValue(pair.Key, out diskVal);
                if (SameValue(beforeVal, pair.Value)) continue;
                if (!SameValue(diskVal, beforeVal) && !SameValue(diskVal, pair.Value))
                    throw new IOException("同一项资料已被其他操作更新，请重新打开后核对。");
                disk[pair.Key] = pair.Value;
            }
        }

        // JSON member order can change between framework serializer threads or app launches.
        private static bool SameValue(object left, object right)
        {
            if (Object.Equals(left, right)) return true;
            var a = left as IDictionary<string, object>; var b = right as IDictionary<string, object>;
            if (a != null && b != null) return a.Count == b.Count && a.All(x => b.ContainsKey(x.Key) && SameValue(x.Value, b[x.Key]));
            var xArray = left as object[]; var yArray = right as object[];
            return xArray != null && yArray != null && xArray.Length == yArray.Length &&
                xArray.Select((x, i) => SameValue(x, yArray[i])).All(equal => equal);
        }

        private static object SortedJson(object value)
        {
            var obj = value as IDictionary<string, object>;
            if (obj != null)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var entry in obj) sorted[entry.Key] = SortedJson(entry.Value);
                return sorted;
            }
            var array = value as object[];
            return array == null ? value : array.Select(SortedJson).ToArray();
        }

        internal static string StableJson(object value)
        {
            var json = AgentData.Json();
            return json.Serialize(SortedJson(json.DeserializeObject(json.Serialize(value))));
        }

        internal static AssetPromptRepair CurrentRepair(AssetItem item, string style)
        {
            var plan = item.PendingRepair;
            return plan != null && !PromptReady(item) && plan.Revision == PromptRevision(item) && plan.Style == style ? plan : null;
        }
    }
}
