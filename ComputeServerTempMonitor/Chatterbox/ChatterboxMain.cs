using ComputeServerTempMonitor.Chatterbox.Models;
using ComputeServerTempMonitor.Common;
using Newtonsoft.Json;
using System.Diagnostics;

namespace ComputeServerTempMonitor.Chatterbox
{
    public class ChatterboxMain
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

        public static async Task<string> DownloadAudioSample(string url, string filename)
        {
            byte[] audioContents = await hc.GetByteArrayAsync(url);
            if (filename == "")
            {
                filename = SharedContext.Instance.GetConfig().Chatterbox.Paths.Voices + "uploaded/" + Guid.NewGuid().ToString("D") + ".mp3";
            }
            else
            {
                filename = SharedContext.Instance.GetConfig().Chatterbox.Paths.Voices + filename + ".mp3";
            }
            File.WriteAllBytes(filename, audioContents);
            return filename;
        }

        public static async Task<string> Generate(string text, string voiceSamplePath, double? exaggeration, double? cfg)
        {
            if (text == null || text == "")
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Chatterbox", "Text must be set for TTS");
                return null; // failed
            }

            Chatterbox.Models.ChatterboxGenerateRequest r = new Models.ChatterboxGenerateRequest();
            r.text = text;
            r.audioPath = voiceSamplePath;
            if(exaggeration.HasValue)
                r.exaggeration = exaggeration.Value;
            if(cfg.HasValue)
                r.cfg_weight = cfg.Value;

            // this calls the API, gets back a filename (after some mins. use async)
            try
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"{SharedContext.Instance.GetConfig().Chatterbox.URL}/generate");
                req.Content = new StringContent(JsonConvert.SerializeObject(r), System.Text.Encoding.UTF8, "application/json");
                //File.WriteAllText(SharedContext.Instance.GetConfig().Chatterbox.Paths.Output + "sending.json", JsonConvert.SerializeObject(r));
                HttpResponseMessage response = await hc.SendAsync(req);
                response.EnsureSuccessStatusCode();

                string body = await response.Content.ReadAsStringAsync();
                ChatterboxGenerateResponse resp = JsonConvert.DeserializeObject<ChatterboxGenerateResponse>(body);
                // encode to mp3 with the ffmpeg that now lives in this project
                string wavPath = SharedContext.Instance.GetConfig().Chatterbox.Paths.Output + resp.output_file;
                string mp3Path = SharedContext.Instance.GetConfig().Chatterbox.Paths.Output + Path.GetFileNameWithoutExtension(resp.output_file) + ".mp3";
                Process p = Process.Start(new ProcessStartInfo("ffmpeg", $"-i {wavPath} -hide_banner -loglevel panic -vn -ar 24000 -ac 2 -q:a 2 {mp3Path}"));
                p.WaitForExit(30000);
                File.Delete(wavPath);
                return mp3Path;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Chatterbox", "Error calling Chatterbox API: " + ex.Message);
                return "";
            }
        }
    }
}
