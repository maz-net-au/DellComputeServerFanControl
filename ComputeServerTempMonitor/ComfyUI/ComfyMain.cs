using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Json;

namespace ComputeServerTempMonitor.ComfyUI
{
    public static class ComfyMain
    {
        const string FLOW_SUFFIX = "_api.json";
        public static int CurrentQueueLength = 0;

        public static Dictionary<string, HistoryResponse> History = new Dictionary<string, HistoryResponse>();
        public static Dictionary<string, GenerationRequest> Requests = new Dictionary<string, GenerationRequest>();

        public static HttpClient hc = new HttpClient();
        public static List<string> GetCheckpoints(string path)
        {
            List<string> models = GetRecursiveFileList(path, "");
            for (int i = 0; i < models.Count; i++)
            {
                models[i] = models[i];
            }
            return models;
        }

        public static void SaveCache()
        {
            File.WriteAllText(Program.config.ComfyUI.Paths.Temp + "_requestCache.json", JsonConvert.SerializeObject(Requests, Formatting.Indented));
            File.WriteAllText(Program.config.ComfyUI.Paths.Temp + "_historyCache.json", JsonConvert.SerializeObject(History, Formatting.Indented));
        }
        public static void LoadCache()
        {
            try
            {
                if (File.Exists(Program.config.ComfyUI.Paths.Temp + "_requestCache.json"))
                {
                    Requests = JsonConvert.DeserializeObject<Dictionary<string, GenerationRequest>>(File.ReadAllText(Program.config.ComfyUI.Paths.Temp + "_requestCache.json"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Request cache unable to be loaded: {ex.Message}");
            }
            try
            {
                if (File.Exists(Program.config.ComfyUI.Paths.Temp + "_historyCache.json"))
                {
                    History = JsonConvert.DeserializeObject<Dictionary<string, HistoryResponse>>(File.ReadAllText(Program.config.ComfyUI.Paths.Temp + "_historyCache.json"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Request cache unable to be loaded: {ex.Message}");
            }
        }

        public static async Task<string> DownloadImage(string url, string filename)
        {
            byte[] imageContents = await hc.GetByteArrayAsync(url);
            File.WriteAllBytes(filename, imageContents);
            return Path.GetFullPath(filename);
        }

        private static List<string> GetRecursiveFileList(string root, string directory)
        {

            List<string> results = new List<string>();
            if (!Directory.Exists(root + directory))
                return results;
            string[] files = Directory.GetFiles(root + directory);
            foreach (string f in files)
            {
                results.Add(f.Replace(root, ""));
            }
            string[] dirs = Directory.GetDirectories(root + directory);
            foreach (string d in dirs)
            {
                results.AddRange(GetRecursiveFileList(root, (d + "\\").Replace(root, "")));
            }
            return results;
        }
        private static List<ComfyUIField> GenerateRandoms(string flowName, List<ComfyUIField> replacements, bool nullOnly = true)
        {
            // check config to see if its a Random<> and generate a new random for it
            foreach (KeyValuePair<string, ComfyUIField> field in Program.config.ComfyUI.Flows[flowName])
            {
                // we only care about randoms
                if (field.Value.Type.StartsWith("Random<"))
                {
                    // find out if this one is set
                    ComfyUIField? rcf = replacements.Where(x => x.NodeTitle == field.Value.NodeTitle && x.Field == field.Value.Field).FirstOrDefault();
                    if (rcf != null && nullOnly)
                        continue; // skip this one this time
                    string type = field.Value.Type.Substring(7, field.Value.Type.Length - 8);
                    if (type == "Integer")
                    {
                        if (rcf != null)
                            rcf.Value = new Random().NextInt64();
                        else
                            replacements.Add(new ComfyUIField(field.Value.NodeTitle, field.Value.Field, new Random().NextInt64()));
                    }
                    else if (type == "Number")
                    {
                        if (rcf != null)
                            rcf.Value = new Random().NextSingle();
                        else
                            replacements.Add(new ComfyUIField(field.Value.NodeTitle, field.Value.Field, new Random().NextSingle()));
                    }
                }
            }
            return replacements;
        }
        public static async Task<HistoryResponse?> Variation(string id, int imageNum, float vary_by, string userId)
        {
            // if we still have the original request, try do a fancy upscale
            if (!History.ContainsKey(id))
            {
                await PopulateHistory(id, 0); // get it if its there
            }
            if (!History.ContainsKey(id))
            {
                // if its not there now, its never going to be. fail
                return null;
            }
            List<ComfyUI.Image> images = new List<ComfyUI.Image>();
            foreach (var node in History[id].outputs)
            {
                foreach (var results in node.Value)
                {
                    images = results.Value.Where(x => x.type == "output").ToList();
                }
            }
            if (images.Count < imageNum)
                return null;
            ComfyUIField img = new ComfyUIField("Load Image", "image", Program.config.ComfyUI.Paths.Outputs + images[imageNum].filename);
            // should this be so hard-coded?
            if (Requests.ContainsKey(id))
            {
                ComfyUIField vb = new ComfyUIField("BasicScheduler", "denoise", vary_by);
                List<ComfyUIField> rep = JsonConvert.DeserializeObject<List<ComfyUIField>>(JsonConvert.SerializeObject(Requests[id].replacements));
                rep.Add(img);
                rep.Add(vb);
                // image needs to be a link to the one we're upscaling
                return await EnqueueRequest(userId, "flux_variation", GenerateRandoms("flux_variation", rep, false));
            }
            return null;
        }
        public static async Task<HistoryResponse?> Upscale(string id, int imageNum, float upscale_by, string userId)
        {
            // if we still have the original request, try do a fancy upscale
            if (!History.ContainsKey(id))
            {
                await PopulateHistory(id, 0); // get it if its there
            }
            if (!History.ContainsKey(id))
            {
                // if its not there now, its never going to be. fail
                return null;
            }
            List<ComfyUI.Image> images = new List<ComfyUI.Image>();
            foreach (var node in History[id].outputs)
            {
                foreach (var results in node.Value)
                {
                    images = results.Value.Where(x => x.type == "output").ToList();
                }
            }
            if (images.Count < imageNum)
                return null;
            ComfyUIField img = new ComfyUIField("Load Image", "image", Program.config.ComfyUI.Paths.Outputs + images[imageNum].filename);
            // should this be so hard-coded?
            if (Requests.ContainsKey(id))
            {
                ComfyUIField ub = new ComfyUIField("Ultimate SD Upscale", "upscale_by", upscale_by);
                List<ComfyUIField> rep = JsonConvert.DeserializeObject<List<ComfyUIField>>(JsonConvert.SerializeObject(Requests[id].replacements));
                rep.Add(img);
                rep.Add(ub);
                // image needs to be a link to the one we're upscaling
                return await EnqueueRequest(userId, "flux_upscale", rep);
            }
            // use the basic upscale if we dont have the original model or prompts
            Console.WriteLine($"Enqueueing new upscale request for {Program.config.ComfyUI.Paths.Outputs + images[imageNum].filename}");
            return await EnqueueRequest(userId, "basic_upscale", new List<ComfyUIField>()
            {
                img
            });
        }
        public static async Task<HistoryResponse?> Regenerate(string id, string userId)
        {
            // need the original request to do this well?
            // what if we had the original flow name, generated new seeds and then ran that into the prompt?
            if (Requests.ContainsKey(id))
            {
                
                List<ComfyUIField> rep = JsonConvert.DeserializeObject<List<ComfyUIField>>(JsonConvert.SerializeObject(Requests[id].replacements));
                Console.WriteLine("Enqueueing new regenerate request");
                return await EnqueueRequest(userId, Requests[id].flowName, GenerateRandoms(Requests[id].flowName, rep, false));
            }
            return null;
        }

        public static async Task<HistoryResponse?> EnqueueRequest(string userId, string flowName, List<ComfyUIField> replacements)
        {
            if (!File.Exists(Program.config.ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX))
            {
                Console.WriteLine($"Flow '{flowName}' not found.");
                return null;
            }
            Dictionary<string, Step>? loadedFlow = JsonConvert.DeserializeObject<Dictionary<string, Step>>(File.ReadAllText(Program.config.ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX));
            if (loadedFlow == null)
            {
                Console.WriteLine($"Flow '{flowName}' unable to be loaded.");
                return null;
            }
            // find all Randoms and fill them in if there's none
            replacements = GenerateRandoms(flowName, replacements);
            foreach (KeyValuePair<string, Step> pair in loadedFlow)
            {
                string title = pair.Value._meta["title"];
                foreach (ComfyUIField field in replacements)
                {
                    // if this is the right node, replace the value
                    if (field.NodeTitle == title)
                    {
                        try
                        {
                            if (field.Object == "")
                            {
                                pair.Value.inputs[field.Field] = field.Value;
                            }
                            else
                            {
                                JObject obj = (JObject)pair.Value.inputs[field.Object];
                                obj[field.Field] = field.Value;
                                pair.Value.inputs[field.Object] = obj;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }
                    }
                }
            }
            PromptRequest prompt = new PromptRequest(loadedFlow);
            string ps = JsonConvert.SerializeObject(prompt);
            //Console.WriteLine(ps);
            try
            {
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, $"{Program.config.ComfyUI.URL}/prompt");
                //hrm.Headers.Add("Content-Type", "application/json");
                hrm.Content = new StringContent(ps, System.Text.Encoding.UTF8, "application/json");
                HttpResponseMessage response = hc.Send(hrm);
                //HttpResponseMessage response = await hc.PostAsJsonAsync($"{host}/prompt", prompt);
                EnqueueResponse? enqueueResponse = JsonConvert.DeserializeObject<EnqueueResponse>(await response.Content.ReadAsStringAsync());
                //Console.WriteLine(JsonConvert.SerializeObject(enqueueResponse));
                if (enqueueResponse == null)
                {
                    Console.WriteLine($"Request not able to be enqueued.");
                    return null;
                }
                if (enqueueResponse.prompt_id == null)
                {
                    Console.WriteLine($"No prompt_id returned");
                    return null;
                }
                Requests.Add(enqueueResponse.prompt_id, new GenerationRequest(flowName, replacements));
                CurrentQueueLength++;
                // so now we have a successfully queued prompt. we should poll the /history for it periodically
                Thread.Sleep(1000);
                bool success = await PopulateHistory(enqueueResponse.prompt_id, 600000);
                if (!success)
                {
                    Console.WriteLine($"A response wasn't generated before the reqeust timed out");
                    return null;
                }
                if (!History.ContainsKey(enqueueResponse.prompt_id))
                    return null;
                CurrentQueueLength--;
                return History[enqueueResponse.prompt_id];
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return new HistoryResponse();
        }

        public static async Task<bool> PopulateHistory(string id, long timeoutMilliseconds = 0)
        {
            if (History.ContainsKey(id))
                return true;
            DateTime start = DateTime.Now;
            HttpResponseMessage response = await hc.GetAsync($"{Program.config.ComfyUI.URL}/history/{id}");
            string body = await response.Content.ReadAsStringAsync();
            int count = 0;
            while (body.Length <= 5) // basically just {} because its not in history yet
            {
                count++;
                if (timeoutMilliseconds > 0)
                {
                    if (count % 6 == 0)
                    {
                        HttpResponseMessage queueRes = await hc.GetAsync($"{Program.config.ComfyUI.URL}/prompt");
                        string content = await queueRes.Content.ReadAsStringAsync();
                        QueueInfo queueInfo = JsonConvert.DeserializeObject<QueueInfo>(content);
                        CurrentQueueLength = queueInfo.exec_info.queue_remaining;
                        if (queueInfo.exec_info.queue_remaining == 0)
                        {
                            // cant be waiting if there's nothing queued
                            return false;
                        }
                    }
                }
                // still processing
                if ((DateTime.Now - start).TotalMilliseconds > timeoutMilliseconds)
                {
                    return false;
                }
                Thread.Sleep(Program.config.ComfyUI.Settings.CompletionPollingRate);
                response = await hc.GetAsync($"{Program.config.ComfyUI.URL}/history/{id}");
                body = await response.Content.ReadAsStringAsync();
            }
            Dictionary<string, HistoryResponse>? historyResponse = JsonConvert.DeserializeObject<Dictionary<string, HistoryResponse>>(body);
            if (historyResponse == null)
                return false;
            foreach (KeyValuePair<string, HistoryResponse> kvp in historyResponse)
            {
                History.Add(kvp.Key, kvp.Value); // there's likely to be just one here
            }
            return true;
        }
        // get workflow names. takes ComfyUIConfig object
        // get vars for a workflow
        // generate workflow takes a workflow name and a map of var names and values
        // for each var in the workflow config, check if its set, if its set then try to place it into the flow
        // send workflow to API
        // this should be in a long running task so i can replace it (15 mins should be fine). pass in the handle to reply to
        // upon complete, attach image to message (somehow) and add buttons to run the generation again, and to upscale
        // do i allow multiple in a batch?
        //
    }
}
