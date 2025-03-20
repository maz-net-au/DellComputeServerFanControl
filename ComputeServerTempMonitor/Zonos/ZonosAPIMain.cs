using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Zonos.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Zonos
{
    public static class ZonosAPIMain
    {
        public static HttpClient hc = new HttpClient();
        public static CancellationToken ct;

        public static void Exit()
        {
            // do we need to do anything when we tear it down? probs cache a list of available files?
            // or can we just get that from the dir listing?
            // store per person?
            // auto embed and delete?

        }

        public static void Init(CancellationToken cancellationToken)
        {
            // do i need to do anything? its really a one-shot api
            ct = cancellationToken;
        }

        public static async Task<byte[]> DownloadAudioSample(string url)
        {
            byte[] audioContents = await hc.GetByteArrayAsync(url);
            return audioContents;
        }

        public static async Task<string> Generate(string text, double speed, string langCode, byte[] voiceSample)
        {
            Zonos.Models.ZonosGenerateRequest r = new Models.ZonosGenerateRequest();
            if (text == null || text == "") 
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Zonos", "Text must be set for TTS");
                return null; // failed
            }

            if(speed < 0.0d)
                speed = 0.0d;

            if (speed > 3.0d)
                speed = 3.0d;


            // it means if langCode isn't set, it's the first letter from the voice
            if (langCode == null || langCode == "")
                langCode = "en-us";

            r.text = text;
            r.speaking_rate = (int)(speed * 10) + 5; // 10 - 20 is the range in their UI
            r.language_iso_code = langCode;
            if (voiceSample != null)
            {
                r.speaker_audio = Convert.ToBase64String(voiceSample);
                //SharedContext.Instance.Log(LogLevel.INFO, "ZonosAPI", $"{r.speaker_audio.Length / 1000000.0}mb of speaker audio attached.");
            }
            // this calls the API, gets back a filename (after some mins. use async)
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"{SharedContext.Instance.GetConfig().Zonos.URL}/v1/audio/text-to-speech");
                req.Headers.Add("X-API-Key", SharedContext.Instance.GetConfig().Zonos.ApiKey);
                req.Content = new StringContent(JsonConvert.SerializeObject(r), System.Text.Encoding.UTF8, "application/json");
                //File.WriteAllText(SharedContext.Instance.GetConfig().Zonos.Paths.Output + "sending.json", JsonConvert.SerializeObject(r));
                HttpResponseMessage response = await hc.SendAsync(req);
                byte[] body = await response.Content.ReadAsByteArrayAsync(); // maybe this is a whole audio file? we'll have to check
                string filename = Guid.NewGuid().ToString("D") + ".mp3";
                if (!Directory.Exists(SharedContext.Instance.GetConfig().Zonos.Paths.Output))
                    Directory.CreateDirectory(SharedContext.Instance.GetConfig().Zonos.Paths.Output);
                File.WriteAllBytes(SharedContext.Instance.GetConfig().Zonos.Paths.Output + filename, body);
                return SharedContext.Instance.GetConfig().Zonos.Paths.Output + filename;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Zonos", "Error calling Zonos API: " + ex.Message);
                return "";
            }
            //if (resp != null)
            //{
            //    HttpResponseMessage audioResp = await hc.GetAsync($"{SharedContext.Instance.GetConfig().Zonos.URL}/generate/{resp.filename}");
            //    if (audioResp != null)
            //    {
            //        await audioResp.Content.ReadAsByteArrayAsync(ct);
            //    }
            //}
        }
    }
}
