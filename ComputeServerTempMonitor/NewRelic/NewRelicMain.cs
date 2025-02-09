using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Hardware;
using ComputeServerTempMonitor.NewRelic.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.NewRelic
{
    public static class NewRelicMain
    {
        private static List<LogMessage> LogMessages = new List<LogMessage>();
        private static List<Event> Events = new List<Event>();
        private static List<Metric> Metrics = new List<Metric>();

        private static CancellationToken ct;
        private static HttpClient webClient = new HttpClient();

        // project name, project subtype (device, website, bot), device type (light, fan, etc), device id
        // this is going to have to handle batching all of the events, metrics and logs
        // when they reach a certain size (1MB) or after a time (15 sec?) send them to new relic
        // gzip them
        // make sure timestamps are added as each item is stored
        // i'll also support some polling system? maybe to reach out to IoT devices?
        // would love to detect / monitor events from them. MQTT finally?
        // support:
        //      server hardware and bot
        //      IoT devices
        //      RPi Hub / hub
        //      network?
        //      kodi
        //      event viewer
        //      ups
        public static void Exit()
        {
            if (Metrics.Count > 0)
            {
                // convert and serialise the metrics array
                string data = JsonConvert.SerializeObject(Metrics);
                data = $"[{{\"metrics\":{data}}}]";
                //Console.WriteLine(data);
                Metrics.Clear();
                if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Metrics, data))
                {

                }
            }
            if (Events.Count > 0)
            {
                // convert and serialise the events array
                string data = JsonConvert.SerializeObject(Events);
                Events.Clear();
                if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Events, data))
                {

                }
            }
            if (LogMessages.Count > 0)
            {
                // convert and serialise the logs array
                string data = JsonConvert.SerializeObject(LogMessages);
                data = $"[{{\"logs\":{data}}}]";
                LogMessages.Clear();
                if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Logs, data))
                {
                    // should we put the data back?
                }
            }
        }

        public static string GetStatus()
        {
            // maybe not used here? i guess it'd report on the number of pending messages and success rate
            return "";
        }

        public static void Init(CancellationToken cancellationToken)
        {
            // init sets up the collections to hold messages and the timer / watches that will push it
            ct = cancellationToken;

            // data logging control thread
            Task tLog = new Task(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (Metrics.Count > 0)
                        {
                            // convert and serialise the metrics array
                            string data = JsonConvert.SerializeObject(Metrics);
                            data = $"[{{\"metrics\":{data}}}]";
                            //Console.WriteLine(data);
                            Metrics.Clear();
                            if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Metrics, data))
                            {

                            }
                        }
                        if (Events.Count > 0)
                        {
                            // convert and serialise the events array
                            string data = JsonConvert.SerializeObject(Events);
                            Events.Clear();
                            if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Events, data))
                            {
                                
                            }
                        }
                        if (LogMessages.Count > 0)
                        {
                            // convert and serialise the logs array
                            string data = JsonConvert.SerializeObject(LogMessages);
                            data = $"[{{\"logs\":{data}}}]";
                            LogMessages.Clear();
                            if (!PushData(SharedContext.Instance.GetConfig().NewRelic.URLs.Logs, data))
                            {
                                // should we put the data back?
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        
                    }
                    Thread.Sleep(SharedContext.Instance.GetConfig().NewRelic.PushInterval * 1000);
                }
            }, ct);
            tLog.Start();
        }
        private static bool PushData(string url, string serialisedData)
        {
            try
            {
                if (SharedContext.Instance.GetConfig().NewRelic.LicenseKey == "")
                    SharedContext.Instance.Log(LogLevel.WARN, "NewRelic", "No license key loaded. No logging will occur.");
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Api-Key", SharedContext.Instance.GetConfig().NewRelic.LicenseKey);
                req.Content = new StringContent(serialisedData, System.Text.Encoding.UTF8, "application/json");
                HttpResponseMessage hrm = webClient.SendAsync(req).Result;
                //Console.WriteLine($"{hrm.StatusCode} - {serialisedData.Length} bytes @ {url}");
                if ((int)hrm.StatusCode >= 400)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "NewRelic", $"New Relic returned {hrm.StatusCode} to a call to {url}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "NewRelic", $"Unable to send data to New Relic ({url}):\n" + ex.ToString());
                return false;
            }
        }
        public static void Log(Metric metrics)
        {
            if(SharedContext.Instance.GetConfig().NewRelic.EnableSending)
                Metrics.Add(metrics);
        }
        public static void Log(LogMessage log)
        {
            if (SharedContext.Instance.GetConfig().NewRelic.EnableSending)
                LogMessages.Add(log);
        }
        public static void Log(Event events)
        {
            if (SharedContext.Instance.GetConfig().NewRelic.EnableSending)
                Events.Add(events);
        }
    }
}
