﻿using Ionic.Zip;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using V2RayGCon.Resource.Resx;

namespace V2RayGCon.Lib
{
    public static class Utils
    {

        #region Json
        /// <summary>
        /// return null if fail
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="a"></param>
        /// <returns></returns>
        public static T Clone<T>(T a) where T : class
        {
            if (a == null)
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(
                    JsonConvert.SerializeObject(a));
            }
            catch { }
            return null;
        }

        /// <summary>
        /// return null if fail
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="content"></param>
        /// <returns></returns>
        public static T DeserializeObject<T>(string content) where T : class
        {
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            try
            {
                var result = JsonConvert.DeserializeObject<T>(content);
                if (result != null)
                {
                    return result;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// return null if fail
        /// </summary>
        /// <param name="serializeObject"></param>
        /// <returns></returns>
        public static string SerializeObject(object serializeObject)
        {
            if (serializeObject == null)
            {
                return null;
            }
            return JsonConvert.SerializeObject(serializeObject);
        }

        public static string GetConfigRoot(bool isInbound, bool isV4)
        {
            return (isInbound ? "inbound" : "outbound")
                + (isV4 ? "s.0" : "");
        }

        public static JObject ParseImportRecursively(
          Func<List<string>, List<string>> fetcher,
          JObject config,
          int depth)
        {
            var empty = JObject.Parse(@"{}");

            if (depth <= 0)
            {
                return empty;
            }

            // var config = JObject.Parse(configString);

            var urls = Lib.Utils.ExtractImportUrlsFrom(config);
            var contents = fetcher(urls);

            if (contents.Count <= 0)
            {
                return config;
            }

            var configList =
                Lib.Utils.ExecuteInParallel<string, JObject>(
                    contents,
                    (content) =>
                    {
                        return ParseImportRecursively(
                            fetcher,
                            JObject.Parse(content),
                            depth - 1);
                    });

            var result = empty;
            foreach (var c in configList)
            {
                Lib.Utils.CombineConfig(ref result, c);
            }
            Lib.Utils.CombineConfig(ref result, config);

            return result;
        }

        public static List<string> ExtractImportUrlsFrom(JObject config)
        {
            List<string> urls = null;
            var empty = new List<string>();
            var import = Lib.Utils.GetKey(config, "v2raygcon.import");
            if (import != null && import is JObject)
            {
                urls = (import as JObject).Properties().Select(p => p.Name).ToList();
            }
            return urls ?? new List<string>();
        }

        public static Dictionary<string, string> GetEnvVarsFromConfig(JObject config)
        {
            var empty = new Dictionary<string, string>();

            var env = GetKey(config, "v2raygcon.env");
            if (env == null)
            {
                return empty;
            }

            try
            {
                return env.ToObject<Dictionary<string, string>>();
            }
            catch (JsonSerializationException)
            {
                return empty;
            }
        }

        public static string GetAliasFromConfig(JObject config)
        {
            var name = GetValue<string>(config, "v2raygcon.alias");
            return string.IsNullOrEmpty(name) ? I18N.Empty : CutStr(name, 12);
        }

        public static string GetSummaryFromConfig(JObject config)
        {
            var result = GetSummaryFromConfig(config, "outbound");

            if (string.IsNullOrEmpty(result))
            {
                return GetSummaryFromConfig(config, "outbounds.0");
            }

            return result;
        }

        public static string GetSummaryFromConfig(JObject config, string root)
        {
            var protocol = GetValue<string>(config, root + ".protocol")?.ToLower();
            if (protocol == null)
            {
                return string.Empty;
            }

            string ipKey = root;
            switch (protocol)
            {
                case "vmess":
                    ipKey += ".settings.vnext.0.address";
                    break;
                case "shadowsocks":
                    protocol = "ss";
                    ipKey += ".settings.servers.0.address";
                    break;
                case "socks":
                    ipKey += ".settings.servers.0.address";
                    break;
            }

            string ip = GetValue<string>(config, ipKey);
            return protocol + (string.IsNullOrEmpty(ip) ? "" : @"@" + ip);
        }

        static bool Contains(JProperty main, JProperty sub)
        {
            return Contains(main.Value, sub.Value);
        }

        static bool Contains(JArray main, JArray sub)
        {
            foreach (var sItem in sub)
            {
                foreach (var mItem in main)
                {
                    if (Contains(mItem, sItem))
                    {
                        return true;
                    }
                }

            }
            return false;
        }

        static bool Contains(JObject main, JObject sub)
        {
            foreach (var item in sub)
            {
                var key = item.Key;
                if (!main.ContainsKey(key))
                {
                    return false;
                }

                if (!Contains(main[key], sub[key]))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool Contains(JValue main, JValue sub)
        {
            return main.Equals(sub);
        }

        public static bool Contains(JToken main, JToken sub)
        {
            if (main.Type != sub.Type)
            {
                return false;
            }

            switch (sub.Type)
            {
                case JTokenType.Property:
                    return Contains(main as JProperty, sub as JProperty);
                case JTokenType.Object:
                    return Contains(main as JObject, sub as JObject);
                case JTokenType.Array:
                    return Contains(main as JArray, sub as JArray);
                default:
                    return Contains(main as JValue, sub as JValue);
            }
        }

        public static Tuple<string, string> ParsePathIntoParentAndKey(string path)
        {
            var index = path.LastIndexOf('.');
            string key;
            string parent = string.Empty;
            if (index < 0)
            {
                key = path;
            }
            else if (index == 0)
            {
                key = path.Substring(1);
            }
            else
            {
                key = path.Substring(index + 1);
                parent = path.Substring(0, index);
            }

            return new Tuple<string, string>(parent, key);
        }

        public static JObject CreateJObject(string path)
        {
            return CreateJObject(path, null);
        }

        public static JObject CreateJObject(string path, JToken child)
        {
            JToken result;
            if (child == null)
            {
                result = JToken.Parse(@"{}");
            }
            else
            {
                result = child;
            }


            if (string.IsNullOrEmpty(path))
            {
                return JObject.Parse(@"{}");
            }

            JToken tempNode;
            foreach (var p in path.Split('.').Reverse())
            {
                if (string.IsNullOrEmpty(p))
                {
                    throw new KeyNotFoundException("Parent contain empty key");
                }

                if (int.TryParse(p, out int num))
                {
                    if (num != 0)
                    {
                        throw new KeyNotFoundException("All parents must be JObject");
                    }
                    tempNode = JArray.Parse(@"[{}]");
                    tempNode[0] = result;
                }
                else
                {
                    tempNode = JObject.Parse(@"{}");
                    tempNode[p] = result;
                }
                result = tempNode;
            }

            return result as JObject;
        }

        public static bool SetValue<T>(JObject json, string path, T value)
        {
            var parts = ParsePathIntoParentAndKey(path);
            var r = json;

            var key = parts.Item2;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var parent = parts.Item1;
            if (!string.IsNullOrEmpty(parent))
            {
                var p = GetKey(json, parent);
                if (p == null || !(p is JObject))
                {
                    return false;
                }
                r = p as JObject;
            }

            r[key] = new JValue(value);
            return true;
        }

        public static bool TryExtractJObjectPart(
            JObject source,
            string path,
            out JObject result)
        {
            var parts = ParsePathIntoParentAndKey(path);
            var key = parts.Item2;
            var parentPath = parts.Item1;
            result = null;

            if (string.IsNullOrEmpty(key))
            {
                // throw new KeyNotFoundException("Key is empty");
                return false;
            }

            var node = GetKey(source, path);
            if (node == null)
            {
                // throw new KeyNotFoundException("This JObject has no key: " + path);
                return false;
            }

            result = CreateJObject(parentPath);

            var parent = string.IsNullOrEmpty(parentPath) ?
                result : GetKey(result, parentPath);

            if (parent == null || !(parent is JObject))
            {
                // throw new KeyNotFoundException("Create parent JObject fail!");
                return false;
            }

            parent[key] = node.DeepClone();
            return true;
        }

        public static void RemoveKeyFromJObject(JObject json, string path)
        {
            var parts = ParsePathIntoParentAndKey(path);

            var parent = parts.Item1;
            var key = parts.Item2;

            if (string.IsNullOrEmpty(key))
            {
                throw new KeyNotFoundException();
            }

            var node = string.IsNullOrEmpty(parent) ?
                json : GetKey(json, parent);

            if (node == null || !(node is JObject))
            {
                throw new KeyNotFoundException();
            }

            (node as JObject).Property(key)?.Remove();
        }

        static void ConcatJson(ref JObject body, JObject mixin)
        {
            body.Merge(mixin, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Concat,
                MergeNullValueHandling = MergeNullValueHandling.Ignore,
            });
        }

        public static void UnionJson(ref JObject body, JObject mixin)
        {
            body.Merge(mixin, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Union,
                MergeNullValueHandling = MergeNullValueHandling.Ignore,
            });

        }

        public static void CombineConfig(ref JObject body, JObject mixin)
        {
            JObject backup = JObject.Parse(@"{}");

            foreach (var key in new string[] {
                    "inbounds",
                    "outbounds",
                    "inboundDetour",
                    "outboundDetour",
                    "routing.settings.rules"})
            {
                if (TryExtractJObjectPart(body, key, out JObject nodeBody))
                {
                    RemoveKeyFromJObject(body, key);
                }

                if (TryExtractJObjectPart(mixin, key, out JObject nodeMixin))
                {
                    ConcatJson(ref backup, nodeMixin);
                    RemoveKeyFromJObject(mixin, key);
                    ConcatJson(ref body, nodeMixin);
                }

                if (nodeBody != null)
                {
                    UnionJson(ref body, nodeBody);
                }
            }

            MergeJson(ref body, mixin);

            // restore mixin
            ConcatJson(ref mixin, backup);
        }

        public static JObject ImportItemList2JObject(
            List<Model.Data.ImportItem> items,
            bool isIncludeSpeedTest,
            bool isIncludeActivate)
        {
            var result = CreateJObject(@"v2raygcon.import");
            foreach (var item in items)
            {
                var url = item.url;
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }
                if ((isIncludeSpeedTest && item.isUseOnSpeedTest)
                    || (isIncludeActivate && item.isUseOnActivate))
                {
                    result["v2raygcon"]["import"][url] = item.alias ?? string.Empty;
                }
            }
            return result;
        }

        public static void MergeJson(ref JObject body, JObject mixin)
        {
            body.Merge(mixin, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Merge,
                MergeNullValueHandling = MergeNullValueHandling.Merge
            });
        }

        /// <summary>
        /// return null if path is null or path not exists.
        /// </summary>
        /// <param name="json"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public static JToken GetKey(JToken json, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var curPos = json;
            var keys = path.Split('.');

            int depth;
            for (depth = 0; depth < keys.Length; depth++)
            {
                if (curPos == null || !curPos.HasValues)
                {
                    break;
                }

                if (int.TryParse(keys[depth], out int n))
                {
                    curPos = curPos[n];
                }
                else
                {
                    curPos = curPos[keys[depth]];
                }
            }

            return depth < keys.Length ? null : curPos;
        }

        public static T GetValue<T>(JToken json, string prefix, string key)
        {
            return GetValue<T>(json, $"{prefix}.{key}");
        }

        /// <summary>
        /// return null if not exist.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="json"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public static T GetValue<T>(JToken json, string path)
        {
            var key = GetKey(json, path);

            var def = default(T) == null && typeof(T) == typeof(string) ?
                (T)(object)string.Empty :
                default(T);

            if (key == null)
            {
                return def;
            }
            try
            {
                return key.Value<T>();
            }
            catch { }
            return def;
        }

        public static Func<string, string, string> GetStringByPrefixAndKeyHelper(JObject json)
        {
            var o = json;
            return (prefix, key) =>
            {
                return GetValue<string>(o, $"{prefix}.{key}");
            };
        }

        public static Func<string, string> GetStringByKeyHelper(JObject json)
        {
            var o = json;
            return (key) =>
            {
                return GetValue<string>(o, $"{key}");
            };
        }

        public static string GetAddr(JObject json, string prefix, string keyIP, string keyPort)
        {
            var ip = GetValue<String>(json, prefix, keyIP) ?? "127.0.0.1";
            var port = GetValue<string>(json, prefix, keyPort);
            return string.Join(":", ip, port);
        }

        #endregion

        #region convert
        public static string Config2String(JObject config)
        {
            return config.ToString(Formatting.None);
        }

        public static string Config2Base64String(JObject config)
        {
            return Base64Encode(config.ToString(Formatting.None));
        }

        public static List<string> Str2ListStr(string serial)
        {
            var list = new List<string> { };
            var items = serial.Split(',');
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item))
                {
                    list.Add(item);
                }

            }
            return list;
        }

        public static List<string> ExtractLinks(string text, Model.Data.Enum.LinkTypes linkType)
        {
            string pattern = GenPattern(linkType);
            var matches = Regex.Matches("\n" + text, pattern, RegexOptions.IgnoreCase);
            var links = new List<string>();
            foreach (Match match in matches)
            {
                links.Add(match.Value.Substring(1));
            }
            return links;
        }

        public static string Vmess2VmessLink(Model.Data.Vmess vmess)
        {
            if (vmess == null)
            {
                return string.Empty;
            }

            string content = JsonConvert.SerializeObject(vmess);
            return AddLinkPrefix(
                Base64Encode(content),
                Model.Data.Enum.LinkTypes.vmess);
        }

        public static Model.Data.Vmess VmessLink2Vmess(string link)
        {
            try
            {
                string plainText = Base64Decode(GetLinkBody(link));
                var vmess = JsonConvert.DeserializeObject<Model.Data.Vmess>(plainText);
                if (!string.IsNullOrEmpty(vmess.add)
                    && !string.IsNullOrEmpty(vmess.port)
                    && !string.IsNullOrEmpty(vmess.aid))
                {

                    return vmess;
                }
            }
            catch { }
            return null;
        }

        public static Model.Data.Shadowsocks SSLink2SS(string ssLink)
        {
            string b64 = GetLinkBody(ssLink);

            try
            {
                var ss = new Model.Data.Shadowsocks();
                var plainText = Base64Decode(b64);
                var parts = plainText.Split('@');
                var mp = parts[0].Split(':');
                if (parts[1].Length > 0 && mp[0].Length > 0 && mp[1].Length > 0)
                {
                    ss.method = mp[0];
                    ss.pass = mp[1];
                    ss.addr = parts[1];
                }
                return ss;
            }
            catch { }
            return null;
        }

        public static Model.Data.Vmess ConfigString2Vmess(string config)
        {
            JObject json;
            try
            {
                json = JObject.Parse(config);
            }
            catch
            {
                return null;
            }

            var GetStr = GetStringByPrefixAndKeyHelper(json);

            Model.Data.Vmess vmess = new Model.Data.Vmess
            {
                v = "2",
                ps = GetStr("v2raygcon", "alias")
            };

            var isUseV4 = (GetStr("outbounds.0", "protocol")?.ToLower()) == "vmess";
            var root = isUseV4 ? "outbounds.0" : "outbound";

            var prefix = root + "." + "settings.vnext.0";
            vmess.add = GetStr(prefix, "address");
            vmess.port = GetStr(prefix, "port");
            vmess.id = GetStr(prefix, "users.0.id");
            vmess.aid = GetStr(prefix, "users.0.alterId");

            prefix = root + "." + "streamSettings";
            vmess.net = GetStr(prefix, "network");
            vmess.type = GetStr(prefix, "kcpSettings.header.type");
            vmess.tls = GetStr(prefix, "security");

            switch (vmess.net)
            {
                case "ws":
                    vmess.path = GetStr(prefix, "wsSettings.path");
                    vmess.host = GetStr(prefix, "wsSettings.headers.Host");
                    break;
                case "h2":
                    try
                    {
                        vmess.path = GetStr(prefix, "httpSettings.path");
                        var hosts = isUseV4 ?
                            json["outbounds"][0]["streamSettings"]["httpSettings"]["host"] :
                            json["outbound"]["streamSettings"]["httpSettings"]["host"];
                        vmess.host = JArray2Str(hosts as JArray);
                    }
                    catch { }
                    break;
            }
            return vmess;
        }

        public static JArray Str2JArray(string content)
        {
            var arr = new JArray();
            var items = content.Replace(" ", "").Split(',');
            foreach (var item in items)
            {
                if (item.Length > 0)
                {
                    arr.Add(item);
                }
            }
            return arr;
        }

        public static string JArray2Str(JArray array)
        {
            if (array == null)
            {
                return string.Empty;
            }
            List<string> s = new List<string>();

            foreach (var item in array.Children())
            {
                try
                {
                    var v = item.Value<string>();
                    if (!string.IsNullOrEmpty(v))
                    {
                        s.Add(v);
                    }
                }
                catch { }
            }

            if (s.Count <= 0)
            {
                return string.Empty;
            }
            return string.Join(",", s);
        }

        public static string Base64Encode(string plainText)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        static string Base64PadRight(string base64)
        {
            return base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        }

        public static string Base64Decode(string base64EncodedData)
        {
            if (string.IsNullOrEmpty(base64EncodedData))
            {
                return string.Empty;
            }
            var padded = Base64PadRight(base64EncodedData);
            var base64EncodedBytes = System.Convert.FromBase64String(padded);
            return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
        }

        #endregion

        #region net
        public static long VisitWebPageSpeedTest(string url = "https://www.google.com", int port = -1)
        {
            var timeout = Str2Int(StrConst.SpeedTestTimeout) * 1000;

            long elasped = long.MaxValue;
            try
            {
                using (WebClient wc = new Lib.Nets.TimedWebClient
                {
                    Encoding = System.Text.Encoding.UTF8,
                    Timeout = timeout,
                })
                {

                    if (port > 0)
                    {
                        wc.Proxy = new WebProxy("127.0.0.1", port);
                    }

                    var result = string.Empty;
                    AutoResetEvent speedTestCompleted = new AutoResetEvent(false);
                    wc.DownloadStringCompleted += (s, a) =>
                    {
                        try
                        {
                            result = a.Result;
                        }
                        catch { }
                        speedTestCompleted.Set();
                    };

                    Stopwatch sw = new Stopwatch();
                    sw.Reset();
                    sw.Start();
                    wc.DownloadStringAsync(new Uri(url));

                    // 收到信号为True
                    if (!speedTestCompleted.WaitOne(timeout))
                    {
                        wc.CancelAsync();
                        return elasped;
                    }
                    sw.Stop();
                    if (!string.IsNullOrEmpty(result))
                    {
                        elasped = sw.ElapsedMilliseconds;
                    }
                }
            }
            catch { }
            return elasped;
        }

        static readonly IPEndPoint _defaultLoopbackEndpoint = new IPEndPoint(IPAddress.Loopback, port: 0);
        public static int GetFreeTcpPort()
        {
            // https://stackoverflow.com/questions/138043/find-the-next-tcp-port-in-net

            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(_defaultLoopbackEndpoint);
                return ((IPEndPoint)socket.LocalEndPoint).Port;
            }
        }

        public static string FetchThroughProxy(string url, int proxyPort)
        {
            var html = string.Empty;

            using (WebClient wc = new Lib.Nets.TimedWebClient
            {
                Encoding = System.Text.Encoding.UTF8,
                Timeout = 30 * 1000,
            })
            {
                wc.Proxy = new WebProxy("127.0.0.1", proxyPort);
                /* 如果用抛出异常的写法
                 * task中调用此函数时
                 * 会弹出用户未处理异常警告
                 */
                try
                {
                    html = wc.DownloadString(url);
                }
                catch { }
            }
            return html;
        }

        public static string Fetch(string url, int timeout = -1)
        {
            var html = string.Empty;

            using (WebClient wc = new Lib.Nets.TimedWebClient
            {
                Encoding = System.Text.Encoding.UTF8,
                Timeout = timeout,
            })
            {
                /* 如果用抛出异常的写法
                 * task中调用此函数时
                 * 会弹出用户未处理异常警告
                 */
                try
                {
                    html = wc.DownloadString(url);
                }
                catch { }
            }
            return html;
        }

        public static string GetLatestVGCVersion()
        {
            string html = Fetch(StrConst.UrlLatestVGC);

            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            string p = StrConst.PatternLatestVGC;
            var match = Regex.Match(html, p, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        public static List<string> GetCoreVersions(int proxyPort)
        {
            List<string> versions = new List<string> { };
            var url = StrConst.ReleasePageUrl;

            string html = proxyPort > 0 ?
                FetchThroughProxy(url, proxyPort) :
                Fetch(url);

            if (string.IsNullOrEmpty(html))
            {
                return versions;
            }

            string pattern = StrConst.PatternDownloadLink;
            var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                var v = match.Groups[1].Value;
                if (!versions.Contains(v))
                {
                    versions.Add(v);
                }
            }

            return versions;
        }
        #endregion

        #region files
        static bool IsTrustedPluginFileName(string path)
        {
            try
            {
                var fileName = Path.GetFileName(path);
                return JsonConvert
                 .DeserializeObject<Dictionary<string, string>>(StrConst.PluginsDebugList)
                 .Keys
                 .ToList()
                 .Contains(fileName);
            }
            catch { }
            return false;
        }

        static bool IsTrustedPluginSha256Sum(string sha256Sum)
            => JsonConvert
                 .DeserializeObject<Dictionary<string, string>>(StrConst.PluginsReleaseList)
                 .Keys
                 .ToList()
                 .Contains(sha256Sum);

        public static bool IsTrustedPlugin(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

#if DEBUG
            return IsTrustedPluginFileName(path);
#else
            return IsTrustedPluginSha256Sum(GetChecksum(path));
#endif
        }

        static string GetChecksum(string file)
        {
            // http://peterkellner.net/2010/11/24/efficiently-generating-sha256-checksum-for-files-using-csharp/
            try
            {
                using (FileStream stream = File.OpenRead(file))
                {
                    var sha = new SHA256Managed();
                    byte[] checksum = sha.ComputeHash(stream);
                    return BitConverter
                        .ToString(checksum)
                        .Replace("-", String.Empty)
                        .ToLower();
                }
            }
            catch { }
            return string.Empty;
        }

        public static string GetSysAppDataFolder()
        {
            var appData = System.Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(appData, Properties.Resources.AppName);
        }

        public static void CreateAppDataFolder()
        {
            var path = GetSysAppDataFolder();

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public static void DeleteAppDataFolder()
        {
            Directory.Delete(GetSysAppDataFolder(), recursive: true);
        }
        #endregion

        #region Miscellaneous
        public static bool AreEqual(double a, double b)
        {
            return Math.Abs(a - b) < 0.000001;
        }

        public static string SHA256(string randomString)
        {
            var crypt = new System.Security.Cryptography.SHA256Managed();
            var hash = new System.Text.StringBuilder();
            byte[] crypto = crypt.ComputeHash(Encoding.UTF8.GetBytes(randomString ?? string.Empty));
            foreach (byte theByte in crypto)
            {
                hash.Append(theByte.ToString("x2"));
            }
            return hash.ToString();
        }

        public static bool PartialMatch(string source, string partial)
        {
            var s = source.ToLower();
            var p = partial.ToLower();

            int idxS = 0, idxP = 0;
            while (idxS < s.Length && idxP < p.Length)
            {
                if (s[idxS] == p[idxP])
                {
                    idxP++;
                }
                idxS++;
            }
            return idxP == p.Length;
        }

        public static string RandomHex(int length)
        {
            //  https://stackoverflow.com/questions/1344221/how-can-i-generate-random-alphanumeric-strings-in-c
            if (length <= 0)
            {
                return string.Empty;
            }

            Random random = new Random();
            const string chars = "0123456789abcdef";
            return new string(
                Enumerable.Repeat(chars, length)
                    .Select(s => s[random.Next(s.Length)])
                    .ToArray());
        }

        public static int Clamp(int value, int min, int max)
        {
            return Math.Max(Math.Min(value, max - 1), min);
        }

        public static int GetIndexIgnoreCase(Dictionary<int, string> dict, string value)
        {
            foreach (var data in dict)
            {
                if (!string.IsNullOrEmpty(data.Value)
                    && data.Value.Equals(value, StringComparison.CurrentCultureIgnoreCase))
                {
                    return data.Key;
                }
            }
            return -1;
        }

        public static string CutStr(string s, int len)
        {

            if (len >= s.Length)
            {
                return s;
            }

            var ellipsis = "...";

            if (len <= 3)
            {
                return ellipsis;
            }

            return s.Substring(0, len - 3) + ellipsis;
        }

        public static int Str2Int(string value)
        {
            if (float.TryParse(value, out float f))
            {
                return (int)Math.Round(f);
            };
            return 0;
        }

        public static bool TryParseIPAddr(string address, out string ip, out int port)
        {
            ip = "127.0.0.1";
            port = 1080;

            int index = address.LastIndexOf(':');
            if (index < 0)
            {
                return false;
            }

            var ipStr = address.Substring(0, index);
            var portStr = address.Substring(index + 1);
            var portInt = Clamp(Str2Int(portStr), 0, 65536);

            if(string.IsNullOrEmpty(ipStr) || portInt == 0)
            {
                return false;
            }

            ip = ipStr;
            port = portInt;
            return true;
        }

        static string GetLinkPrefix(Model.Data.Enum.LinkTypes linkType)
        {
            return Model.Data.Table.linkPrefix[(int)linkType];
        }

        public static string GenPattern(Model.Data.Enum.LinkTypes linkType)
        {
            return string.Format(
               "{0}{1}{2}",
               StrConst.PatternNonAlphabet, // vme[ss]
               GetLinkPrefix(linkType),
               StrConst.PatternBase64);
        }

        public static string AddLinkPrefix(string b64Content, Model.Data.Enum.LinkTypes linkType)
        {
            return GetLinkPrefix(linkType) + b64Content;
        }

        public static string GetLinkBody(string link)
        {
            Regex re = new Regex("[a-zA-Z0-9]+://");
            return re.Replace(link, string.Empty);
        }

        public static void ZipFileDecompress(string zipFile, string outFolder)
        {
            // let downloader handle exception
            using (ZipFile zip = ZipFile.Read(zipFile))
            {
                var flattenFoldersOnExtract = zip.FlattenFoldersOnExtract;
                zip.FlattenFoldersOnExtract = true;
                zip.ExtractAll(outFolder, ExtractExistingFileAction.OverwriteSilently);
                zip.FlattenFoldersOnExtract = flattenFoldersOnExtract;
            }
        }
        #endregion

        #region UI related
        public static void CopyToClipboardAndPrompt(string content)
        {
            MessageBox.Show(
                Lib.Utils.CopyToClipboard(content) ?
                I18N.CopySuccess :
                I18N.CopyFail);
        }

        public static bool CopyToClipboard(string content)
        {
            try
            {
                Clipboard.SetText(content);
                return true;
            }
            catch { }
            return false;
        }

        public static string GetAppDir()
        {
            // The code below will fail in test.
            // return Path.GetDirectoryName(Application.ExecutablePath);

            // https://stackoverflow.com/questions/6041332/best-way-to-get-application-folder-path/35295609
            return System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        public static void SupportProtocolTLS12()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }
            catch (System.NotSupportedException)
            {
                MessageBox.Show(I18N.SysNotSupportTLS12);
            }
        }

        public static string GetClipboardText()
        {
            if (Clipboard.ContainsText(TextDataFormat.Text))
            {
                return Clipboard.GetText(TextDataFormat.Text);

            }
            return string.Empty;
        }
        #endregion

        #region task and process

        /*
         * ChainActionHelper loops from [count - 1] to [0]
         * 
         * These integers will pass into function worker 
         * through the first parameter,
         * which is index in this example one by one.
         * 
         * The second parameter (next) of function worker
         * is generated automatically for chaining up these actions.
         * 
         * Action<int,Action> worker = (index, next)=>{
         * 
         *   // do something accroding to index
         *   Debug.WriteLine(index); 
         *   
         *   // call next when done
         *   next(); 
         * }
         * 
         * Action done = ()=>{
         *   // do something when all done
         *   // or simply set to null
         * }
         * 
         * Finally call this function like this.
         * ChainActionHelper(10, worker, done);
         */

        public static void ChainActionHelperAsync(int countdown, Action<int, Action> worker, Action done = null)
        {
            Task.Factory.StartNew(() =>
            {
                ChainActionHelperWorker(countdown, worker, done)();
            });
        }

        // wrapper
        public static void ChainActionHelper(int countdown, Action<int, Action> worker, Action done = null)
        {
            ChainActionHelperWorker(countdown, worker, done)();
        }

        static Action ChainActionHelperWorker(int countdown, Action<int, Action> worker, Action done = null)
        {
            int _index = countdown - 1;

            return () =>
            {
                if (_index < 0)
                {
                    done?.Invoke();
                    return;
                }

                worker(_index, ChainActionHelperWorker(_index, worker, done));
            };
        }

        public static List<TResult> ExecuteInParallel<TParam, TResult>(List<TParam> values, Func<TParam, TResult> lambda)
        {
            var result = new List<TResult>();

            if (values.Count <= 0)
            {
                return result;
            }

            var taskList = new List<Task<TResult>>();
            foreach (var value in values)
            {
                var task = new Task<TResult>(() => lambda(value));
                taskList.Add(task);
                task.Start();
            }
            try
            {
                Task.WaitAll(taskList.ToArray());
            }
            catch (AggregateException ae)
            {
                foreach (var e in ae.InnerExceptions)
                {
                    throw e;
                }
            }

            foreach (var task in taskList)
            {
                result.Add(task.Result);
                task.Dispose();
            }

            return result;
        }

        public static void RunAsSTAThread(Action lambda)
        {
            // https://www.codeproject.com/Questions/727531/ThreadStateException-cant-handeled-in-ClipBoard-Se
            AutoResetEvent @event = new AutoResetEvent(false);
            Thread thread = new Thread(
                () =>
                {
                    lambda();
                    @event.Set();
                });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            @event.WaitOne();
        }

        public static void KillProcessAndChildrens(int pid)
        {
            ManagementObjectSearcher processSearcher = new ManagementObjectSearcher
              ("Select * From Win32_Process Where ParentProcessID=" + pid);
            ManagementObjectCollection processCollection = processSearcher.Get();

            // We must kill child processes first!
            if (processCollection != null)
            {
                foreach (ManagementObject mo in processCollection)
                {
                    KillProcessAndChildrens(Convert.ToInt32(mo["ProcessID"])); //kill child processes(also kills childrens of childrens etc.)
                }
            }

            // Then kill parents.
            try
            {
                Process proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
            }
            catch
            {
                // Process already exited.
            }
        }
        #endregion

        #region for Testing
        public static string[] TestingGetResourceConfigJson()
        {
            return new string[]
            {
                StrConst.config_example,
                StrConst.config_min,
                StrConst.config_tpl,
                StrConst.config_pkg,
                StrConst.PluginsDebugList,
                StrConst.PluginsReleaseList,
            };
        }
        #endregion
    }
}
