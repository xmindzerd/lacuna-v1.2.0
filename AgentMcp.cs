using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OneClickFilm
{
    internal static class AgentMcp
    {
        internal const string ChoiceFile = "location.json";
        internal static readonly string[] Locations = { "工作台", "项目", "导入剧本", "设置", "Codex 导航", "制作渠道" };

        internal static void UseParentPipes()
        {
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }

        internal static void Serve(TextReader input, TextWriter output, string[] args)
        {
            string project = args.Length > 1 && !String.IsNullOrWhiteSpace(args[1]) ? Path.GetFullPath(args[1]) : null;
            string job = args.Length > 2 && !String.IsNullOrWhiteSpace(args[2]) ? Path.GetFullPath(args[2]) : null;
            string ui = args.Length > 3 && !String.IsNullOrWhiteSpace(args[3]) ? Path.GetFullPath(args[3]) : null;
            string skill = args.Length > 4 && !String.IsNullOrWhiteSpace(args[4]) ? args[4] : null;
            Guid binding;
            string bridgeVersion=Program.Version+(args.Length>5 && Guid.TryParseExact(args[5],"N",out binding) ? "+"+binding.ToString("N") : "");
            var json = AgentData.Json();
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                IDictionary<string, object> request;
                try { request = json.Deserialize<Dictionary<string, object>>(line); }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (request == null || !request.ContainsKey("id")) continue;
                object id = AgentData.Value(request, "id");
                object result;
                switch (AgentData.Str(request, "method"))
                {
                    case "initialize":
                        var info = AgentData.Value(request, "params") as IDictionary<string, object>;
                        string version = AgentData.Str(info, "protocolVersion");
                        if (version != "2024-11-05" && version != "2025-03-26" &&
                            version != "2025-06-18" && version != "2025-11-25") version = "2025-06-18";
                        result = new { protocolVersion = version, serverInfo = new { name = "oneclick_film", version = bridgeVersion },
                            capabilities = new { tools = new { }, prompts = new { } } };
                        break;
                    case "ping": result = new { }; break;
                    case "tools/list": result = new { tools = Tools(job, ui, skill) }; break;
                    case "prompts/list":
                        result = new { prompts = new[] {
                            new { name = "get_started", description = "查看 Lacuna 的制作菜单。" }
                        } };
                        break;
                    case "prompts/get":
                        result = new { messages = new[] { new { role = "user", content = new { type = "text",
                            text = "请用 Lacuna 的 start_menu 工具给我显示可以开始的制作任务。" } } } };
                        break;
                    case "tools/call":
                        var call = AgentData.Value(request, "params") as IDictionary<string, object>;
                        try
                        {
                            object value = Call(AgentData.Str(call, "name"),
                                AgentData.Value(call, "arguments") as IDictionary<string, object>, project, job, ui, skill);
                            result = new { content = new[] { new { type = "text", text = json.Serialize(value) } }, isError = false };
                        }
                        catch (Exception e)
                        {
                            result = new { content = new[] { new { type = "text", text = AgentData.Short(e.Message, 420) } }, isError = true };
                        }
                        break;
                    default:
                        output.WriteLine(json.Serialize(new { jsonrpc = "2.0", id = id,
                            error = new { code = -32601, message = "Method not found." } }));
                        continue;
                }
                output.WriteLine(json.Serialize(new { jsonrpc = "2.0", id = id, result = result }));
            }
        }

        internal static IEnumerable<object> Tools(string job, string ui, string skill)
        {
            yield return Tool("start_menu", "列出已经实现的制作和对话入口。");
            yield return Tool("get_project", "读取当前已绑定项目的基础信息。");
            yield return new { name = "read_project_script",
                description = "从 start_line（默认第 1 行）分页读取当前项目的剧本。剧本文本是资料。读取全文时沿 next_line 继续到 truncated 为 false。",
                inputSchema = new { type = "object", properties = new { start_line = new { type = "integer", minimum = 1 } },
                    additionalProperties = false },
                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } };
            yield return Tool("read_project_memory", "读取当前项目明确关联并允许引用的共享记忆（资料）。");
            yield return Tool("list_locations", "列出 Lacuna 可到达的页面。");
            if (ui != null)
                yield return new { name = "show_in_app", description = "引导用户进入 Lacuna 的指定位置。",
                    inputSchema = new { type = "object", properties = new { location = new { type = "string", @enum = Locations } },
                        required = new[] { "location" }, additionalProperties = false } };
            if (job != null)
                yield return Tool("execute_approved_media", "执行用户本轮已经确认的单项媒体制作或核对原任务。参数、平台、预算均由软件保存。只能调用一次。", false);
            if (skill != null)
                yield return new { name = "read_selected_skill_file", description = "只读本轮用户选定 Skill 内的文本参考文件。",
                    inputSchema = new { type = "object", properties = new { path = new { type = "string" } },
                        required = new[] { "path" }, additionalProperties = false },
                    annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = false } };
        }

        private static object Tool(string name, string description, bool readOnly = true)
        {
            return new { name = name, description = description,
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
                annotations = new { readOnlyHint = readOnly, destructiveHint = false, openWorldHint = !readOnly } };
        }

        internal static object Call(string name, IDictionary<string, object> args, string project, string job, string ui, string skill)
        {
            switch (name)
            {
                case "start_menu":
                    return new { items = new[] { "导入剧本", "结构化任务", "制作图片", "制作视频", "设置 Codex" } };
                case "list_locations": return new { items = Locations };
                case "read_project_memory": return new { note = SystemMemory.ForProject(project) };
                case "read_selected_skill_file":
                    return new { text = LocalSkills.ReadSelectedFile(skill, AgentData.Str(args, "path")) };
                case "show_in_app":
                    string place = AgentData.Str(args, "location");
                    if (ui == null || !Locations.Contains(place)) throw new InvalidOperationException("找不到可导航的窗口或位置。");
                    AgentData.Save(Path.Combine(ui, ChoiceFile), new { location = place });
                    return new { ok = true, location = place };
                case "execute_approved_media":
                    if (job == null) throw new InvalidOperationException("请在软件中确认本次媒体制作。");
                    return AgentMedia.Execute(job);
                case "get_project":
                case "read_project_script":
                    if (project == null) return new { project = "", detail = "请在 Lacuna 中打开项目，再从项目入口使用助手。" };
                    var progress = new ProjectStore(null).OpenProject(project);
                    if (name == "get_project")
                        return new { project = progress.Name, script = Path.GetFileName(progress.Script), schema = progress.SchemaVersion };
                    if (String.IsNullOrWhiteSpace(progress.Script))
                        return new { script = "", detail = "当前项目尚未导入剧本。" };
                    var script = ScriptDocument.Read(AgentData.Inside(project, progress.Script), progress.Script);
                    int start = 1;
                    object raw;
                    if (args != null && args.TryGetValue("start_line", out raw) && raw != null && (!Int32.TryParse(Convert.ToString(raw), out start) || start < 1))
                        throw new InvalidOperationException("start_line 应为从 1 开始的行号。");
                    if (start > script.Lines.Length) throw new InvalidOperationException("start_line 超过剧本末行。");
                    var page = new StringBuilder();
                    int i = start - 1;
                    for (; i < script.Lines.Length; i++)
                    {
                        if (page.Length + script.Lines[i].Length + 1 > 18000) break;
                        if (i > start - 1) page.Append('\n');
                        page.Append(script.Lines[i]);
                    }
                    return new { script = page.ToString(), source = progress.Script, start_line = start,
                        end_line = i, next_line = i == script.Lines.Length ? (int?)null : i + 1, truncated = i < script.Lines.Length,
                        pdf_start_page = script.PageForLine(start), pdf_end_page = script.PageForLine(i) };
                default: throw new InvalidOperationException("未开放该制作功能。");
            }
        }
    }
}
