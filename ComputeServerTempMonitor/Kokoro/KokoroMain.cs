using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Kokoro.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Kokoro
{
    public static class KokoroMain
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

        public static async Task<string> Generate(string text, string voice, double speed, string langCode)
        {
            Kokoro.Models.KokoroGenerateRequest r = new Models.KokoroGenerateRequest();
            if (text == null || text == "") 
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Kokoro", "Text must be set for TTS");
                return null; // failed
            }
            if (voice == null || voice == "")
                voice = "af_heart";
            if(speed <= 0.0d)
                speed = 1.0d;

            // it means if langCode isn't set, it's the first letter from the voice
            if (langCode == null || langCode == "")
                langCode = voice.Substring(0, 1);

            r.text = text;
            r.voice = voice;
            r.speed = speed;
            r.lang_code = langCode;
            // this calls the API, gets back a filename (after some mins. use async)

            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"{SharedContext.Instance.GetConfig().Kokoro.URL}/generate/");
            req.Content = new StringContent(JsonConvert.SerializeObject(r), System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage response = await hc.SendAsync(req);
            string body = await response.Content.ReadAsStringAsync();
            KokoroGenerateResponse resp = JsonConvert.DeserializeObject<KokoroGenerateResponse>(body);
            // encode to mp3 with the ffmpeg that now lives in this project
            Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-i {SharedContext.Instance.GetConfig().Kokoro.Paths.Output + resp.filename} -hide_banner -loglevel panic -vn -ar 24000 -ac 2 -q:a 2 {SharedContext.Instance.GetConfig().Kokoro.Paths.Output}{Path.GetFileNameWithoutExtension(resp.filename)}.mp3"));
            p.WaitForExit(30000);
            File.Delete(SharedContext.Instance.GetConfig().Kokoro.Paths.Output + resp.filename);
            return SharedContext.Instance.GetConfig().Kokoro.Paths.Output + Path.GetFileNameWithoutExtension(resp.filename) + ".mp3";
            
            //if (resp != null)
            //{
            //    HttpResponseMessage audioResp = await hc.GetAsync($"{SharedContext.Instance.GetConfig().Kokoro.URL}/generate/{resp.filename}");
            //    if (audioResp != null)
            //    {
            //        await audioResp.Content.ReadAsByteArrayAsync(ct);
            //    }
            //}
        }
    }
}
