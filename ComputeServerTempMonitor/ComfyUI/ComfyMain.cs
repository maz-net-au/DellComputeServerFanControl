using ComputeServerTempMonitor.ComfyUI.Models;
using ComputeServerTempMonitor.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Json;
using YamlDotNet.Core.Tokens;

namespace ComputeServerTempMonitor.ComfyUI
{
    public static class ComfyMain
    {
        const string FLOW_SUFFIX = "_api.json";
        const string historyFile = "data/comfyHistoryCache.json";
        const string requestFile = "data/comfyRequestCache.json";
        static CancellationToken cancellationToken;
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
        
        private static void SaveCache()
        {
            File.WriteAllText(requestFile, JsonConvert.SerializeObject(Requests, Formatting.Indented));
            File.WriteAllText(historyFile, JsonConvert.SerializeObject(History, Formatting.Indented));
        }

        public static void Exit()
        {
            SaveCache();
        }
        public static void Init(CancellationToken ct)
        {
            cancellationToken = ct;
            try
            {
                if (File.Exists(requestFile))
                {
                    Requests = JsonConvert.DeserializeObject<Dictionary<string, GenerationRequest>>(File.ReadAllText(requestFile));
                }
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "ComfyMain", $"Request cache unable to be loaded: {ex.Message}");
            }
            try
            {
                if (File.Exists(historyFile))
                {
                    History = JsonConvert.DeserializeObject<Dictionary<string, HistoryResponse>>(File.ReadAllText(historyFile));
                }
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "ComfyMain", $"Request cache unable to be loaded: {ex.Message}");
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
            foreach (KeyValuePair<string, ComfyUIField> field in SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields)
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
                        {
                            rcf.Value = new Random().NextInt64();
                        }
                        else
                        {
                            var f = new ComfyUIField(field.Value.NodeTitle, field.Value.Field, new Random().NextInt64());
                            f.Type = "Random<Integer>";
                            replacements.Add(f);
                        }
                    }
                    else if (type == "Number")
                    {
                        if (rcf != null)
                        {
                            rcf.Value = new Random().NextSingle();
                        }
                        else
                        {
                            var f = new ComfyUIField(field.Value.NodeTitle, field.Value.Field, new Random().NextSingle());
                            f.Type = "Random<Number>";
                            replacements.Add(f);
                        }
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
            List<Image> images = new List<Image>();
            foreach (var node in History[id].outputs)
            {
                foreach (var results in node.Value)
                {
                    images = results.Value.Where(x => x.type == "output").ToList();
                }
            }
            if (images.Count < imageNum)
                return null;
            ComfyUIField img = new ComfyUIField("Load Image", "image", SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename);
            img.Type = "Attachment";
            // should this be so hard-coded?
            if (Requests.ContainsKey(id))
            {
                ComfyUIField vb = new ComfyUIField("BasicScheduler", "denoise", vary_by);
                vb.Type = "Number";
                List<ComfyUIField> rep = JsonConvert.DeserializeObject<List<ComfyUIField>>(JsonConvert.SerializeObject(Requests[id].replacements));
                rep.Add(img);
                rep.Add(vb);
                // add all the defaults from the original flow?
                // how do i find the original flow if its a variation of a variation?
                if (Requests.ContainsKey(id))
                {

                    if ((Requests[id].type & FlowModelTypes.flux) == FlowModelTypes.flux)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new flux variation request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "flux_variation", GenerateRandoms("flux_variation", rep, false));
                    }
                    else if ((Requests[id].type & FlowModelTypes.sdxl) == FlowModelTypes.sdxl)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new sd variation request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "sd_variation", GenerateRandoms("sd_variation", rep, false));
                    }
                    else if ((Requests[id].type & FlowModelTypes.sd35) == FlowModelTypes.sd35)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new sd3.5 variation request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "sd35_variation", GenerateRandoms("sd35_variation", rep, false));
                    }
                }
                // image needs to be a link to the one we're upscaling
            }
            return null;
        }
        public static async Task<HistoryResponse?> Upscale(string id, int imageNum, float upscale_by, string userId)
        {
            // if we still have the original request, try do a fancy upscale
            if (!History.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"History does not contain '{id}', attempting to retrieve from ComfyUI");
                await PopulateHistory(id, 0); // get it if its there
            }
            if (!History.ContainsKey(id))
            {
                // if its not there now, its never going to be. fail
                SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Could not retrieve '{id}' from ComfyUI");
                return null;
            }
            List<Image> images = new List<Image>();
            foreach (var node in History[id].outputs)
            {
                foreach (var results in node.Value)
                {
                    images = results.Value.Where(x => x.type == "output").ToList();
                }
            }
            if (images.Count < imageNum)
                return null;
            ComfyUIField img = new ComfyUIField("Load Image", "image", SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename);
            img.Type = "Attachment";
            // should this be so hard-coded?
            if (Requests.ContainsKey(id))
            {
                ComfyUIField ub = new ComfyUIField("Ultimate SD Upscale", "upscale_by", upscale_by);
                ub.Type = "Number";
                List<ComfyUIField> rep = JsonConvert.DeserializeObject<List<ComfyUIField>>(JsonConvert.SerializeObject(Requests[id].replacements));
                rep.Add(img);
                rep.Add(ub);
                // image needs to be a link to the one we're upscaling
                // feed in the default values for the other flow in case they're important?
                // do we want to do this wholesale?
                if (Requests.ContainsKey(id))
                {

                    if ((Requests[id].type & FlowModelTypes.flux) == FlowModelTypes.flux)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new flux upscale request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "flux_upscale", rep);
                    }
                    else if ((Requests[id].type & FlowModelTypes.sdxl) == FlowModelTypes.sdxl)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new sd upscale request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "full_upscale", rep);
                    }
                    else if ((Requests[id].type & FlowModelTypes.sd35) == FlowModelTypes.sd35)
                    {
                        SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new sd3.5 upscale request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
                        return await EnqueueRequest(userId, "sd35_upscale", rep);
                    }
                }
            }
            // use the basic upscale if we dont have the original model or prompts
            SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Enqueueing new basic upscale request for {SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + images[imageNum].filename}");
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
                SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", "Enqueueing new regenerate request");
                return await EnqueueRequest(userId, Requests[id].flowName, GenerateRandoms(Requests[id].flowName, rep, false));
            }
            SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"Requests does not contain '{id}'");
            return null;
        }

        public static List<ComfyUIField> FillDefaults(string flowName, List<ComfyUIField> replacements)
        {
            //List<ComfyUIField> defaults = new List<ComfyUIField>();
            if (!File.Exists(SharedContext.Instance.GetConfig().ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX))
            {
                SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"Flow '{flowName}' not found.");
                return null;
            }
            Dictionary<string, Step>? loadedFlow = JsonConvert.DeserializeObject<Dictionary<string, Step>>(File.ReadAllText(SharedContext.Instance.GetConfig().ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX));
            if (loadedFlow == null)
            {
                SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"Flow '{flowName}' unable to be loaded.");
                return null;
            }
            // I need to search the flow for any parameter in the config
            Dictionary<string, ComfyUIField> allParams = SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields;
            foreach (KeyValuePair<string, Step> pair in loadedFlow)
            {
                string title = pair.Value._meta["title"];
                foreach (KeyValuePair<string, ComfyUIField> field in allParams)
                {
                    ComfyUIField? rcf = replacements.Where(x => x.NodeTitle == field.Value.NodeTitle && x.Field == field.Value.Field).FirstOrDefault();
                    if (rcf != null)
                        continue; // skip ones we already have values for
                    if (field.Value.NodeTitle == title)
                    {
                        try
                        {
                            if (field.Value.Object == "")
                            {
                                var f = new ComfyUIField(title, field.Value.Field, pair.Value.inputs[field.Value.Field], field.Value.Object);
                                f.Type = field.Value.Type;
                                f.Filter = field.Value.Filter;
                                replacements.Add(f);
                            }
                            else
                            {
                                // get the whole object as the value?
                                var obj = pair.Value.inputs[field.Value.Object] as JObject;
                                // peel the one value out of it at a time
                                var f = new ComfyUIField(title, field.Value.Field, obj[field.Value.Field], field.Value.Object);
                                f.Type = field.Value.Type;
                                f.Filter = field.Value.Filter;
                                replacements.Add(f);
                            }
                        }
                        catch (Exception ex)
                        {
                            SharedContext.Instance.Log(LogLevel.ERR, "ComfyMain", ex.ToString());
                        }
                    }
                }
            }
            return replacements;
        }

        public static async Task<HistoryResponse?> EnqueueRequest(string userId, string flowName, List<ComfyUIField> replacements)
        {
            if (!File.Exists(SharedContext.Instance.GetConfig().ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX))
            {
                SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"Flow '{flowName}' not found.");
                return null;
            }
            Dictionary<string, Step>? loadedFlow = JsonConvert.DeserializeObject<Dictionary<string, Step>>(File.ReadAllText(SharedContext.Instance.GetConfig().ComfyUI.Paths.Prompts + flowName + FLOW_SUFFIX));
            if (loadedFlow == null)
            {
                SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"Flow '{flowName}' unable to be loaded.");
                return null;
            }
            // find all Randoms and fill them in if there's none
            replacements = GenerateRandoms(flowName, replacements);
            // fill in any defaults
            replacements = FillDefaults(flowName, replacements);
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
                            SharedContext.Instance.Log(LogLevel.ERR, "ComfyMain", ex.ToString());
                        }
                    }
                }
            }
            PromptRequest prompt = new PromptRequest(loadedFlow);
            string ps = JsonConvert.SerializeObject(prompt);
            //Console.WriteLine(ps);
            try
            {
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, $"{SharedContext.Instance.GetConfig().ComfyUI.URL}/prompt");
                //hrm.Headers.Add("Content-Type", "application/json");
                hrm.Content = new StringContent(ps, System.Text.Encoding.UTF8, "application/json");
                HttpResponseMessage response = hc.Send(hrm);
                //HttpResponseMessage response = await hc.PostAsJsonAsync($"{host}/prompt", prompt);
                EnqueueResponse? enqueueResponse = JsonConvert.DeserializeObject<EnqueueResponse>(await response.Content.ReadAsStringAsync());
                //Console.WriteLine(JsonConvert.SerializeObject(enqueueResponse));
                if (enqueueResponse == null)
                {
                    SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"Request not able to be enqueued.");
                    return null;
                }
                if (enqueueResponse.prompt_id == null)
                {
                    SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"No prompt_id returned");
                    File.WriteAllText("data/errorFlow.json", ps);
                    return null;
                }
                SharedContext.Instance.Log(LogLevel.INFO, "ComfyMain", $"New prompt accepted {enqueueResponse.prompt_id}");
                Requests.Add(enqueueResponse.prompt_id, new GenerationRequest(flowName, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Type, replacements));
                CurrentQueueLength++;
                // so now we have a successfully queued prompt. we should poll the /history for it periodically
                Thread.Sleep(1000);
                bool success = await PopulateHistory(enqueueResponse.prompt_id, 600000);
                if (!success)
                {
                    SharedContext.Instance.Log(LogLevel.WARN, "ComfyMain", $"A response wasn't generated before the reqeust timed out");
                    return null;
                }
                if (!History.ContainsKey(enqueueResponse.prompt_id))
                    return null;
                CurrentQueueLength--;
                return History[enqueueResponse.prompt_id];
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "ComfyMain", ex.Message);
            }
            return new HistoryResponse();
        }

        public static async Task<bool> PopulateHistory(string id, long timeoutMilliseconds = 0)
        {
            if (History.ContainsKey(id))
                return true;
            DateTime start = DateTime.Now;
            HttpResponseMessage response = await hc.GetAsync($"{SharedContext.Instance.GetConfig().ComfyUI.URL}/history/{id}");
            string body = await response.Content.ReadAsStringAsync();
            int count = 0;
            while (body.Length <= 5) // basically just {} because its not in history yet
            {
                count++;
                if (timeoutMilliseconds > 0)
                {
                    if (count % 6 == 0)
                    {
                        HttpResponseMessage queueRes = await hc.GetAsync($"{SharedContext.Instance.GetConfig().ComfyUI.URL}/prompt");
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
                Thread.Sleep(SharedContext.Instance.GetConfig().ComfyUI.Settings.CompletionPollingRate);
                response = await hc.GetAsync($"{SharedContext.Instance.GetConfig().ComfyUI.URL}/history/{id}");
                body = await response.Content.ReadAsStringAsync();
            }
            Dictionary<string, HistoryResponse>? historyResponse = JsonConvert.DeserializeObject<Dictionary<string, HistoryResponse>>(body);
            if (historyResponse == null)
                return false;
            foreach (KeyValuePair<string, HistoryResponse> kvp in historyResponse)
            {
                History.Add(kvp.Key, kvp.Value); // there's likely to be just one here
            }
            SaveCache(); // write out the request cache
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
