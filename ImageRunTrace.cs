using System;
using System.IO;
using System.Text;

namespace OneClickFilm
{
    internal sealed class ImageRunTrace
    {
        private const string FileName = "image-phases.txt";
        private readonly string folder;
        private readonly object gate = new object();
        private string last;
        internal ImageRunTrace(string work) { folder = work; }

        internal void Mark(string phase)
        {
            lock (gate)
            {
                if (last == phase) return;
                try
                {
                    File.AppendAllText(Path.Combine(folder, FileName), DateTime.UtcNow.ToString("o") + "  " + phase + Environment.NewLine, new UTF8Encoding(false));
                    last = phase;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal void Event(AgentEvent e)
        {
            switch (e.Type)
            {
                case "session": Mark("本地会话已建立"); break;
                case "started": Mark("生成轮次已开始，等待平台结果"); break;
                case "retry": Mark("请求连接中断，Codex 正在重连 " + e.Text); break;
                case "image_started": Mark("图像工具已开始生成"); break;
                case "image_finished": Mark("图像工具已结束，等待本地回图"); break;
                case "step": Mark("模型正在调用制作工具"); break;
                case "tool_error": Mark("制作工具返回错误"); break;
                case "reasoning": Mark("模型正在处理请求"); break;
                case "notice": Diagnostic(e.Text ?? ""); break;
                case "image_progress_message": StoreReply(e.Text); break;
                case "message":
                    Mark("模型已回复");
                    StoreReply(e.Text);
                    break;
                case "complete": Mark("模型已结束本轮"); break;
                case "failed": Mark("模型返回失败事件"); break;
            }
        }

        private void StoreReply(string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return;
            try
            {
                string text = AgentData.Short(message.Trim(), 850);
                if (System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"(?i)bearer\s|authorization|api[_-]?key|(?:sk|sess)-[a-z0-9]|https?://\S+\?")) return;
                File.WriteAllText(Path.Combine(folder, "image-model-reply.txt"), text, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal void Diagnostic(string line)
        {
            // Only persist fixed categories. Diagnostic streams may contain prompts, headers or image data.
            var s = line.ToLowerInvariant();
            if (s.Contains("falling back to http")) Mark("WebSocket 连接回退到 HTTPS");
            else if (s.Contains("stream disconnected") || s.Contains("error sending request") || s.Contains("connection reset"))
                Mark("与生成服务的响应流断开");
            else if (s.Contains("websocket") && (s.Contains("timed out") || s.Contains("failed")))
                Mark("WebSocket 连接失败");
            else if (s.Contains("429 too many requests")) Mark("平台返回请求过于频繁");
        }

        internal static string Last(string folder)
        {
            try
            {
                string path = Path.Combine(folder, FileName);
                if (!File.Exists(path)) return null;
                string lastLine = null;
                foreach (string line in File.ReadLines(path)) if (line.Length > 30) lastLine = line;
                return lastLine == null ? null : AgentData.Short(lastLine.Substring(30), 100);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
