﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using static V2RayGCon.Lib.StringResource;

namespace V2RayGCon.Service
{
    class Setting : Model.BaseClass.SingletonService<Setting>
    {
        private Model.Data.EventList<string> servers;
        private List<string> aliases;
        private List<string[]> servSummarys;
        public event EventHandler OnSettingChange;
        public event EventHandler<Model.Data.StrEvent> OnLog;
        public event EventHandler OnRequireCoreRestart;

        private object addServerLock;

        Setting()
        {
            LoadServers();
            SaveServers();

            isSysProxyHasSet = false;
            addServerLock = new object();

            aliases = new List<string>();
            servSummarys = new List<string[]>();
            OnServerListChanged();
            servers.ListChanged += OnServerListChanged;
        }

        #region Properties

        public bool isSysProxyHasSet;

        public int GetCurServIndex()
        {
            return _curServIndex;
        }

        int _curServIndex
        {
            get
            {
                // bug: return Lib.Utils.Clamp(n, 0, ServNum);
                return Math.Max(0, Properties.Settings.Default.CurServ);
            }
            set
            {
                int n = Lib.Utils.Clamp(value, 0, GetServerCount());
                Properties.Settings.Default.CurServ = n;
                Properties.Settings.Default.Save();
            }
        }

        public bool isShowConfigureToolsPanel
        {
            get
            {
                return Properties.Settings.Default.CfgShowToolPanel == true;
            }
            set
            {
                Properties.Settings.Default.CfgShowToolPanel = value;
                Properties.Settings.Default.Save();
            }
        }

        public int maxLogLines
        {
            get
            {
                int n = Properties.Settings.Default.MaxLogLine;
                return Lib.Utils.Clamp(n, 10, 1000);
            }
            set
            {
                int n = Lib.Utils.Clamp(value, 10, 1000);
                Properties.Settings.Default.MaxLogLine = n;
                Properties.Settings.Default.Save();
            }
        }

        public string proxyAddr
        {
            get
            {
                string addr = Properties.Settings.Default.ProxyAddr;
                Lib.Utils.TryParseIPAddr(addr, out string ip, out int port);
                return string.Join(":", ip, port);
            }
            set
            {
                Properties.Settings.Default.ProxyAddr = value;
                Properties.Settings.Default.Save();
            }
        }

        public int proxyType
        {
            get
            {
                var type = Properties.Settings.Default.ProxyType;
                return Lib.Utils.Clamp(type, 0, 3);
            }
            set
            {
                Properties.Settings.Default.ProxyType = value;
                Properties.Settings.Default.Save();
            }
        }

        #endregion

        #region public methods
        public void SendLog(string log)
        {
            var arg = new Model.Data.StrEvent(log);
            OnLog?.Invoke(this, arg);
        }

        public string GetCurAlias()
        {
            var aliases = GetAllAliases();
            if (aliases.Count < 1)
            {
                return string.Empty;
            }
            var index = GetCurServIndex();
            index = Lib.Utils.Clamp(index, 0, aliases.Count);
            return aliases[index];
        }

        public string GetInbountInfo()
        {
            var b64 = GetServer(GetCurServIndex());

            if (string.IsNullOrEmpty(b64))
            {
                return string.Empty;
            }

            var config = JObject.Parse(Lib.Utils.Base64Decode(b64));

            try
            {
                var proxy = string.Format(
                    "{0}://{1}:{2}",
                    config["inbound"]["protocol"],
                    config["inbound"]["listen"],
                    config["inbound"]["port"]);

                return proxy;
            }
            catch { }

            return string.Empty;
        }

        public int GetServerCount()
        {
            return servers.Count;
        }

        public void MoveItemToTop(int index)
        {
            var n = Lib.Utils.Clamp(index, 0, GetServerCount());

            if (_curServIndex == n)
            {
                _curServIndex = 0;
            }
            else if (_curServIndex < n)
            {
                _curServIndex++;
            }

            var item = servers[n];
            servers.RemoveAtQuiet(n);
            servers.Insert(0, item);
            SaveServers();
            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public void MoveItemUp(int index)
        {
            if (index < 1 || index > GetServerCount() - 1)
            {
                return;
            }
            if (_curServIndex == index)
            {
                _curServIndex--;
            }
            else if (_curServIndex == index - 1)
            {
                _curServIndex++;
            }
            var item = servers[index];
            servers.RemoveAtQuiet(index);
            servers.Insert(index - 1, item);
            SaveServers();

            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public void MoveItemDown(int index)
        {
            if (index < 0 || index > GetServerCount() - 2)
            {
                return;
            }
            if (_curServIndex == index)
            {
                _curServIndex++;
            }
            else if (_curServIndex == index + 1)
            {
                _curServIndex--;
            }
            var item = servers[index];
            servers.RemoveAtQuiet(index);
            servers.Insert(index + 1, item);
            SaveServers();
            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public void MoveItemToButtom(int index)
        {
            var n = Lib.Utils.Clamp(index, 0, GetServerCount());

            if (_curServIndex == n)
            {
                _curServIndex = GetServerCount() - 1;
            }
            else if (_curServIndex > n)
            {
                _curServIndex--;
            }
            var item = servers[n];
            servers.RemoveAtQuiet(n);
            servers.Add(item);
            SaveServers();
            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public void DeleteAllServers()
        {
            servers.Clear();
            SaveServers();
            OnSettingChange?.Invoke(this, EventArgs.Empty);
            OnRequireCoreRestart?.Invoke(this, EventArgs.Empty);
        }

        public void DeleteServer(int index)
        {
            Debug.WriteLine("delete server: " + index);

            if (index < 0 || index >= GetServerCount())
            {
                Debug.WriteLine("index out of range");
                return;
            }

            servers.RemoveAt(index);
            SaveServers();

            if (_curServIndex >= GetServerCount())
            {
                // normal restart
                _curServIndex = GetServerCount() - 1;
                OnRequireCoreRestart?.Invoke(this, EventArgs.Empty);
            }
            else if (_curServIndex == index)
            {
                // force restart
                OnRequireCoreRestart?.Invoke(this, EventArgs.Empty);
            }
            else if (_curServIndex > index)
            {
                // do not need restart
                _curServIndex--;
            }

            // do not need restart
            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public void ImportLinks(string links)
        {
            var that = this;

            var tasks = new Task<Tuple<bool, List<string[]>>>[] {
                new Task<Tuple<bool, List<string[]>>>(()=>ImportVmessLinks(links)),
                new Task<Tuple<bool, List<string[]>>>(()=>ImportV2RayLinks(links)),
                new Task<Tuple<bool, List<string[]>>>(()=>ImportSSLinks(links)),
                new Task<Tuple<bool, List<string[]>>>(()=>ImportVLinks(links)),
            };

            Task.Factory.StartNew(()=> {
                // import all links
                foreach(var task in tasks)
                {
                    task.Start();
                }
                
                Task.WaitAll(tasks);

                // get results
                var allResults = new List<string[]>();
                var isAddNewServer = false;

                foreach(var task in tasks)
                {
                    isAddNewServer = isAddNewServer || task.Result.Item1;
                    allResults.AddRange(task.Result.Item2);
                    task.Dispose();
                }

                // show results
                if (isAddNewServer)
                {
                    servers.Notify();
                    OnSettingChange?.Invoke(that, EventArgs.Empty);
                }

                if (allResults.Count > 0)
                {
                    new Views.FormImportLinksResult(allResults);
                    Application.Run();
                }
                else
                {
                    MessageBox.Show(I18N("NoLinkFound"));
                }
            });
        }

        public ReadOnlyCollection<string> GetAllAliases()
        {
            return aliases.AsReadOnly();
        }

        public void LoadServers()
        {
            servers = new Model.Data.EventList<string>();

            List<string> serverList = null;
            try
            {
                serverList = JsonConvert.DeserializeObject<List<string>>(
                    Properties.Settings.Default.Servers);
                if (serverList == null)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            // make sure every server config can be parsed
            for (var i = serverList.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (JObject.Parse(Lib.Utils.Base64Decode(serverList[i])) == null)
                    {
                        serverList.RemoveAt(i);
                    }
                }
                catch
                {
                    serverList.RemoveAt(i);
                }
            }

            servers = new Model.Data.EventList<string>(serverList);
        }

        public ReadOnlyCollection<string[]> GetAllServerSummarys()
        {
            return servSummarys.AsReadOnly();
        }

        public ReadOnlyCollection<string> GetAllServers()
        {
            return servers.AsReadOnly();
        }

        public int GetServerIndex(string b64Server)
        {
            return servers.IndexOf(b64Server);
        }

        public string GetServer(int index)
        {
            if (GetServerCount() == 0
                || index < 0
                || index >= GetServerCount())
            {
                return string.Empty;
            }

            return servers[index];
        }

        public bool AddServer(string b64ConfigString, bool quiet = false)
        {
            var result = false;

            lock (addServerLock)
            {
                if (GetServerIndex(b64ConfigString) < 0)
                {
                    result = true;
                    if (quiet)
                    {
                        servers.AddQuiet(b64ConfigString);
                    }
                    else
                    {
                        servers.Add(b64ConfigString);
                    }
                    SaveServers();
                }
            }

            if (result && !quiet)
            {
                try
                {
                    OnSettingChange?.Invoke(this, EventArgs.Empty);
                }
                catch { }
            }
            
            return result;
        }

        public void ActivateServer(int index = -1)
        {
            if (index >= 0)
            {
                _curServIndex = index;
            }
            OnRequireCoreRestart?.Invoke(this, EventArgs.Empty);
            OnSettingChange?.Invoke(this, EventArgs.Empty);
        }

        public bool ReplaceServer(string b64ConfigString, int index)
        {
            if (index < 0 || index >= GetServerCount())
            {
                return AddServer(b64ConfigString);
            }

            if (GetServerIndex(b64ConfigString) >= 0)
            {
                SendLog(I18N("DuplicateServer") + "\r\n");
                return false;
            }

            servers[index] = b64ConfigString;
            SaveServers();

            if (index == _curServIndex)
            {
                OnRequireCoreRestart?.Invoke(this, EventArgs.Empty);
            }
            OnSettingChange?.Invoke(this, EventArgs.Empty);
            return true;
        }

        #endregion

        #region private method

        Tuple<bool, List<string[]>> ImportSSLinks(string text)
        {
            var isAddNewServer = false;
            var links = Lib.Utils.ExtractLinks(text, Model.Data.Enum.LinkTypes.ss);
            List<string[]> result = new List<string[]>();

            foreach (var link in links)
            {
                string config = Lib.Utils.SSLink2ConfigString(link);
                if (string.IsNullOrEmpty(config))
                {
                    result.Add(GenImportResult(link, false, I18N("DecodeFail")));
                    continue;
                }

                if (AddServer(Lib.Utils.Base64Encode(config), true))
                {
                    isAddNewServer = true;
                    result.Add(GenImportResult(link, true, I18N("Success")));
                }
                else
                {
                    result.Add(GenImportResult(link, false, I18N("DuplicateServer")));
                }
            }

            return new Tuple<bool, List<string[]>>(isAddNewServer, result);
        }

        Tuple<bool, List<string[]>> ImportV2RayLinks(string text)
        {
            bool isAddNewServer = false;
            var links = Lib.Utils.ExtractLinks(text, Model.Data.Enum.LinkTypes.v2ray);
            List<string[]> result = new List<string[]>();

            foreach (var link in links)
            {
                try
                {
                    string b64Config = Lib.Utils.GetLinkBody(link);
                    string config = Lib.Utils.Base64Decode(b64Config);
                    if (JObject.Parse(config) != null)
                    {
                        if (AddServer(b64Config, true))
                        {
                            isAddNewServer = true;
                            result.Add(GenImportResult(link, true, I18N("Success")));
                        }
                        else
                        {
                            result.Add(GenImportResult(link, false, I18N("DuplicateServer")));
                        }
                    }
                }
                catch
                {
                    // skip if error occured
                    result.Add(GenImportResult(link, false, I18N("DecodeFail")));
                }
            }

            return new Tuple<bool, List<string[]>>(isAddNewServer, result);
        }

        string[] GenImportResult(string link,bool success,string reason)
        {
            return new string[]
            {
                string.Empty,  // reserve for index
                link,
                success?"√":"×",
                reason,
            };
        }

        Task<Tuple<bool, string[]>>  GenDecodeVLinkTask(string link)
        {
            return new Task<Tuple<bool, string[]>>(()=> {
                var _isAddNewServer = true;
                var _result = GenImportResult(link, true, I18N("Success"));

                try
                {
                    var timeout = Lib.Utils.Str2Int(resData("ImportVLinkTimeOut")) * 1000;
                    var config = Lib.VLinkCodec.DecodeLink(link, timeout).ToString();
                    if (!AddServer(Lib.Utils.Base64Encode(config), true))
                    {
                        _isAddNewServer = false;
                        _result = GenImportResult(link, false, I18N("DuplicateServer"));
                    }
                }
                catch (FormatException)
                {
                    _result = GenImportResult(link, false, I18N("DecodeFail"));
                }
                catch (System.Net.WebException)
                {
                    _result = GenImportResult(link, false, I18N("NetworkTimeout"));
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    _result = GenImportResult(link, false, I18N("DecodeFail"));
                }

                return new Tuple<bool, string[]>(_isAddNewServer, _result);
            });
        }

        Tuple<bool, List<string[]>> ImportVLinks(string text)
        {
            var isAddNewServer = false;
            var result = new List<string[]>();
            var links = Lib.Utils.ExtractLinks(text, Model.Data.Enum.LinkTypes.v);
            var tasks =new List<Task<Tuple<bool, string[]>>>();

            foreach(var link in links)
            {
                var task = GenDecodeVLinkTask(link);
                tasks.Add(task);
                task.Start();
            }

            Task.WaitAll(tasks.ToArray());

            foreach(var task in tasks)
            {
                isAddNewServer = isAddNewServer || task.Result.Item1;
                result.Add(task.Result.Item2);
                task.Dispose();
            }

            return new Tuple<bool, List<string[]>>(isAddNewServer, result);
        }

        Tuple<bool,List<string[]>> ImportVmessLinks(string text)
        {
            var links = Lib.Utils.ExtractLinks(text, Model.Data.Enum.LinkTypes.vmess);
            var result = new List<string[]>();
            var isAddNewServer = false;

            foreach (var link in links)
            {
                var vmess = Lib.Utils.VmessLink2Vmess(link);
                string config = Lib.Utils.Vmess2ConfigString(vmess);
                if (string.IsNullOrEmpty(config))
                {
                    result.Add(GenImportResult(link,false,I18N("DecodeFail")));
                    continue;
                }
                
                if (AddServer(Lib.Utils.Base64Encode(config),true))
                {
                    result.Add(GenImportResult(link, true, I18N("Success")));
                    isAddNewServer = true;
                }
                else
                {
                    result.Add(GenImportResult(link, false, I18N("DuplicateServer")));
                }
            }

            return new Tuple<bool, List<string[]>>( isAddNewServer, result );
        }

        string GetAliasFromConfig(JObject config)
        {
            var name = Lib.Utils.GetValue<string>(config, "v2raygcon.alias");

            return
                string.IsNullOrEmpty(name) ?
                I18N("Empty") :
                Lib.Utils.CutStr(name, 20);
        }

        string[] GetSummaryFromConfig(JObject config)
        {
            var summary= new string[] {
                string.Empty,  // reserve for no.
                string.Empty,  // reserve for alias
                string.Empty,
                string.Empty,  // ip
                string.Empty,  // port
                string.Empty,  // reserve for activate
                string.Empty,  // stream type
                string.Empty,  // wspath
                string.Empty,  // tls
                string.Empty,  // mKCP disguise
            };

            var GetStr = Lib.Utils.GetStringByKeyHelper(config);

            var protocol = GetStr("outbound.protocol");

            if (string.IsNullOrEmpty(protocol))
            {
                return summary;
            }

            summary[2] = protocol;
            if (protocol == "vmess" || protocol == "shadowsocks")
            {
                var keys = Model.Data.Table.servInfoKeys[protocol];
                summary[3]= GetStr(keys[0]);
                summary[4]= GetStr(keys[1]);
                summary[6]= GetStr(keys[4]);
                summary[7]= GetStr(keys[3]);
                summary[8]= GetStr(keys[2]);
                summary[9]= GetStr(keys[5]);
            }
            
            return summary;
        }

        void OnServerListChanged()
        {
            aliases.Clear();
            servSummarys.Clear();

            for (int i = 0; i < servers.Count; i++)
            {
                try
                {
                    var configString = Lib.Utils.Base64Decode(servers[i]);
                    var config = JObject.Parse(configString);

                    var alias = GetAliasFromConfig(config);
                    aliases.Add(string.Join(".", i + 1, alias));

                    var summary = GetSummaryFromConfig(config);
                    summary[0] = (i + 1).ToString();
                    summary[1] = alias;
                    servSummarys.Add(summary);
                }
                catch { }
            }
        }

        void SaveServers()
        {
            string json = JsonConvert.SerializeObject(servers);
            Properties.Settings.Default.Servers = json;
            Properties.Settings.Default.Save();
        }

        #endregion
    }
}
