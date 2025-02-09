using ComputeServerTempMonitor.NewRelic;
using ComputeServerTempMonitor.NewRelic.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Common
{
    public sealed class SharedContext
    {
        private SharedContext() { } // hidden constructor

        private static SharedContext instance = null;
        private static object syncRoot = new object();
        private static HttpClient webClient = new HttpClient();

        private Config _config = new Config();
        public static SharedContext Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (syncRoot)
                    {
                        if (instance == null)
                        {
                            instance = new SharedContext();
                        }
                    }
                }
                return instance;
            }
        }

        

        public Config GetConfig()
        {
            return _config;
        }
        public void LoadConfig(string path)
        {
            if (File.Exists(path))
            {
                Log(LogLevel.INFO, "Main", "Loading config file");
                _config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();
            }
            else
            {
                Log(LogLevel.INFO, "Main", "config.json not found. Creating default config.");
                _config = new Config();
                File.WriteAllText(path, JsonConvert.SerializeObject(_config));
            }
        }
        public void SaveConfig(string path)
        {
            if (_config == null)
                _config = new Config();
            File.WriteAllText(path, JsonConvert.SerializeObject(_config));
        }

        public void Log(LogLevel lvl, string source, string message)
        {
            // a threadsafe queue
            Console.WriteLine($"{DateTime.Now.ToString("s")}\t{Enum.GetName(lvl).PadRight(4, ' ')}\t{source}: {message}");
            if (_config.NewRelic.ForwardLogs)
                NewRelicMain.Log(new LogMessage() { message = message, level = Enum.GetName(lvl) });
        }

        public void LogMetrics(string eventType, Dictionary<string, object> metrics)
        {
            metrics["eventType"] = eventType;
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _config.NewRelic.URLs.Events);
            req.Headers.Add("Api-Key", _config.NewRelic.LicenseKey);
            req.Content = new StringContent(JsonConvert.SerializeObject(metrics), System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage hrm = webClient.SendAsync(req).Result;
            //Console.WriteLine(hrm.StatusCode + "\n" + hrm.Content.ReadAsStringAsync().Result);
        }

        public static List<string> ExecuteCLI(string command, string args)
        {
            List<string> retMessage = new List<string>();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            Process p = new Process();

            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardInput = true;

            startInfo.WorkingDirectory = Path.GetDirectoryName(command) ?? "";
            startInfo.UseShellExecute = false;
            startInfo.Arguments = args;
            startInfo.FileName = command;

            p.StartInfo = startInfo;
            p.OutputDataReceived += (sender, line) =>
            {
                retMessage.Add(line.Data ?? "");
            };

            p.Start();
            p.BeginOutputReadLine();
            p.WaitForExit();
            p.CancelOutputRead();

            return retMessage;
        }
    }

    public enum LogLevel { DBG, INFO, WARN, ERR }
}
