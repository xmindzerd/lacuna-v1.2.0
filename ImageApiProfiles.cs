using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace OneClickFilm
{
    internal static class ImageApiProfiles
    {
        private const string Prefix = "image-api-";
        internal const string Images = "images";
        internal const string Responses = "responses";

        internal static bool IsProfile(string id)
        {
            Guid unused;
            return id != null && id.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(id.Substring(Prefix.Length), "N", out unused);
        }

        internal static bool IsImages(AssetChannel channel)
        {
            return channel != null && IsProfile(channel.Id) && channel.Driver == Images;
        }

        internal static AssetChannel Get(string id)
        {
            var channel = AssetChannels.Load().FirstOrDefault(c => c.Id == id);
            if (channel != null) return channel;
            throw new InvalidOperationException("此项目的图片 API 配置不在本机，请在设置添加或重新选择出图渠道。");
        }

        private static string KeyFileId(AssetChannel channel)
        {
            if (!IsProfile(channel.Id)) throw new InvalidDataException("图片渠道编号无效。");
            if (String.IsNullOrEmpty(channel.ImageKeyRevision)) return channel.Id; // PR #1 legacy DPAPI file
            return channel.Id + "-" + AgentData.Hash(channel.BaseUrl + "\n" + channel.ImageKeyRevision);
        }

        internal static string Key(AssetChannel channel)
        {
            string key = AnalysisKeyStore.Read(KeyFileId(channel));
            if (String.IsNullOrEmpty(key)) throw new InvalidOperationException("请在「设置 → 自定义图片 API」保存此渠道的 API Key。");
            return key;
        }

        internal static void Store(AssetChannel channel, string key)
        {
            channel.BaseUrl = CodexAuthMode.ValidateUrl(channel.BaseUrl);
            if (String.IsNullOrWhiteSpace(channel.Name) || !AgentRunner.ValidModel(channel.ImageModel ?? ""))
                throw new ArgumentException("请填写渠道名称和图片模型 ID。");
            if (channel.Driver == Responses) CodexAuthMode.Preflight(channel.BaseUrl, channel.MainModel);
            using (AssetChannels.LockForWrite())
            {
                var channels = AssetChannels.Load();
                int index = channels.FindIndex(c => c.Id == channel.Id && IsProfile(c.Id));
                var old = index < 0 ? null : channels[index];
                bool replaceKey = !String.IsNullOrWhiteSpace(key);
                if (!replaceKey && (old == null || old.BaseUrl != channel.BaseUrl))
                    throw new ArgumentException("新渠道或更换 Base URL 时，请填写该地址对应的 API Key。");

                if (channel.Id == null) channel.Id = Prefix + Guid.NewGuid().ToString("N");
                channel.ImageKeyRevision = replaceKey ? AgentData.Id() : old.ImageKeyRevision;
                string keyFile = KeyFileId(channel);
                if (replaceKey) AnalysisKeyStore.Save(key, keyFile);
                try
                {
                    channel.Status = "已配置";
                    if (old == null) channels.Add(channel);
                    else channels[index] = channel;
                    // Publish the address and its credential together, keeping the project's channel ID stable.
                    AssetChannels.Save(channels);
                }
                catch
                {
                    if (replaceKey) AnalysisKeyStore.DiscardImageKey(keyFile);
                    throw;
                }
                if (replaceKey && old != null) AnalysisKeyStore.DiscardImageKey(KeyFileId(old));
            }
        }

        internal static string Generate(AssetChannel channel, string work, string prompt, IList<string> references,
            string size, int timeout, CancellationToken cancel)
        {
            string key = Key(channel);
            bool edit = references != null && references.Count != 0;
            var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var fields = new Dictionary<string, object> { { "model", channel.ImageModel }, { "prompt", prompt }, { "n", 1 } };
            if (!String.IsNullOrEmpty(size)) fields["size"] = ImageOutputSettings.Ratio(size) == "4:3" ? "1536x1024" : size;
            var request = (HttpWebRequest)WebRequest.Create(channel.BaseUrl + (edit ? "/images/edits" : "/images/generations"));
            request.Method = "POST";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = timeout > 0 ? timeout : 360000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
            request.ContentType = "application/json";
            byte[] body;
            if (edit)
            {
                string boundary = "lacuna-" + Guid.NewGuid().ToString("N");
                request.ContentType = "multipart/form-data; boundary=" + boundary;
                using (var form = new MemoryStream())
                {
                    Action<string> add = value => { byte[] bytes = Encoding.UTF8.GetBytes(value); form.Write(bytes, 0, bytes.Length); };
                    foreach (var field in fields)
                        add("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + field.Key + "\"\r\n\r\n" + field.Value + "\r\n");
                    for (int i = 0; i < references.Count; i++)
                    {
                        string ext = Path.GetExtension(references[i]).ToLowerInvariant();
                        string mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : "image/png";
                        add("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" +
                            (references.Count == 1 ? "image" : "image[]") + "\"; filename=\"reference-" + i + ext + "\"\r\nContent-Type: " + mime + "\r\n\r\n");
                        using (var file = File.OpenRead(references[i])) file.CopyTo(form);
                        add("\r\n");
                    }
                    add("--" + boundary + "--\r\n");
                    body = form.ToArray();
                }
            }
            else body = Encoding.UTF8.GetBytes(serializer.Serialize(fields));

            Dictionary<string, object> result;
            string requestId;
            try
            {
                cancel.ThrowIfCancellationRequested();
                using (cancel.Register(request.Abort))
                {
                    request.ContentLength = body.Length;
                    AssetUsageLedger.Dispatch(channel.Name, true);
                    using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        if ((int)response.StatusCode >= 300) throw new IOException("图片 API 返回重定向，请填写最终 API 地址。");
                        requestId = response.Headers["x-request-id"];
                        result = serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    }
                }
            }
            catch (WebException error)
            {
                cancel.ThrowIfCancellationRequested();
                using (var response = error.Response as HttpWebResponse)
                {
                    if (response != null)
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 400 && status < 500 && status != 408) OpenAiResponsesClient.RecordFailure(work, "");
                        throw new IOException("图片 API 返回 HTTP " + status +
                            (edit && (status == 400 || status == 404 || status == 405 || status == 422) ?
                                "。请确认所选模型支持参考图与 /images/edits，以及所选尺寸。" : "。"));
                    }
                    throw new IOException(error.Status == WebExceptionStatus.Timeout ? "图片 API 请求超时。" : "无法连接图片 API。");
                }
            }
            var usage = CodexAppServer.Child(result, "usage");
            if (usage != null && usage.ContainsKey("input_tokens") && usage.ContainsKey("output_tokens"))
                AssetUsageLedger.ModelResponse(new AgentResult { HasUsage = true, Input = AgentData.Number(usage, "input_tokens"),
                    Output = AgentData.Number(usage, "output_tokens") });
            var image = CodexAppServer.Items(result, "data").FirstOrDefault();
            if (image == null) throw new IOException("图片 API 没有返回图片。");
            string data = AgentData.Str(image, "b64_json");
            if (String.IsNullOrEmpty(data))
            {
                Uri imageUrl;
                if (!Uri.TryCreate(AgentData.Str(image, "url"), UriKind.Absolute, out imageUrl) ||
                    imageUrl.Scheme != "https" || imageUrl.UserInfo.Length != 0)
                    throw new IOException("图片 API 未返回图片数据或 HTTPS 下载地址。");
                var download = (HttpWebRequest)WebRequest.Create(imageUrl);
                download.AllowAutoRedirect = false;
                download.Timeout = download.ReadWriteTimeout = request.Timeout;
                using (cancel.Register(download.Abort))
                using (var response = (HttpWebResponse)download.GetResponse())
                using (var buffer = new MemoryStream())
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw new IOException("无法下载图片 API 的结果。");
                    using (var stream = response.GetResponseStream()) stream.CopyTo(buffer);
                    return OpenAiResponsesClient.SaveImageBytes(work, buffer.ToArray(), requestId);
                }
            }
            return OpenAiResponsesClient.SaveImageBytes(work, Convert.FromBase64String(data), requestId);
        }
    }

    internal sealed partial class Workspace
    {
        private void AddImageApiSettings()
        {
            var section = SystemSection("自定义图片 API");
            SystemDetail(section, "与 Codex 登录独立。保存后，在项目的「资产出图渠道」选择此配置。Key 只保存在当前 Windows 用户下。");
            var profiles = AssetChannels.Load().Where(c => ImageApiProfiles.IsProfile(c.Id)).ToList();
            var selector = new ComboBox { ItemsSource = new[] { "新建图片渠道" }.Concat(profiles.Select(c => c.Name)).ToList(), SelectedIndex = 0 };
            section.Children.Add(selector);
            Func<AssetChannel> selected = () => selector.SelectedIndex > 0 ? profiles[selector.SelectedIndex - 1] : null;
            section.Children.Add(Muted("渠道名称", 13, 8));
            var name = CodexTextBox("渠道名称", "", 32);
            section.Children.Add(name);
            section.Children.Add(Muted("接口类型", 13, 8));
            var format = new ComboBox { ItemsSource = new[] { "OpenAI 兼容 Images API", "Responses API 生图" }, SelectedIndex = 0 };
            section.Children.Add(format);
            section.Children.Add(Muted("Base URL", 13, 8));
            var endpoint = CodexTextBox("图片 API Base URL", CodexAuthMode.DefaultUrl, 32);
            section.Children.Add(endpoint);
            section.Children.Add(Muted("API Key（更换地址时须重新填写）", 13, 8));
            var secret = new PasswordBox { MinHeight = 32 };
            section.Children.Add(secret);
            section.Children.Add(Muted("图片模型 ID（按供应商文档填写）", 13, 8));
            var model = CodexTextBox("图片模型 ID", "", 32);
            section.Children.Add(model);

            var advanced = new StackPanel { Visibility = Visibility.Collapsed };
            advanced.Children.Add(Muted("主模型 ID（需支持 image_generation）", 13, 8));
            var main = CodexTextBox("主模型 ID", "", 32);
            advanced.Children.Add(main);
            advanced.Children.Add(Muted("思考强度（供应商支持时再改）", 13, 8));
            var effort = new ComboBox { ItemsSource = new[] { "供应商默认", "低", "中", "高" }, SelectedIndex = 0 };
            string[] effortIds = { "", "low", "medium", "high" };
            advanced.Children.Add(effort);
            section.Children.Add(advanced);
            format.SelectionChanged += delegate { advanced.Visibility = format.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; };
            selector.SelectionChanged += delegate
            {
                var channel = selected();
                name.Text = channel == null ? "" : channel.Name;
                endpoint.Text = channel == null ? CodexAuthMode.DefaultUrl : channel.BaseUrl;
                model.Text = channel == null ? "" : channel.ImageModel;
                main.Text = channel == null ? "" : channel.MainModel;
                format.SelectedIndex = channel != null && channel.Driver == ImageApiProfiles.Responses ? 1 : 0;
                effort.SelectedIndex = channel == null ? 0 : Math.Max(0, Array.IndexOf(effortIds, channel.ImageEffort));
                secret.Clear();
            };
            section.Children.Add(ActionButton("保存图片渠道", delegate
            {
                var previous = selected();
                bool responses = format.SelectedIndex == 1;
                if (!TryAction(delegate
                {
                    ImageApiProfiles.Store(new AssetChannel {
                        Id = previous == null ? null : previous.Id,
                        Name = name.Text.Trim(), BaseUrl = endpoint.Text.Trim(), ImageModel = model.Text.Trim(),
                        Driver = responses ? ImageApiProfiles.Responses : ImageApiProfiles.Images,
                        MainModel = responses ? main.Text.Trim() : null,
                        ImageEffort = responses ? effortIds[Math.Max(0, effort.SelectedIndex)] : null
                    }, secret.Password);
                })) return;
                ShowSettings();
                SetStatus("图片 API 渠道已保存。");
            }, true));
        }
    }
}
