using ComputeServerTempMonitor.Anthropic.Models;
using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Discord;
using ComputeServerTempMonitor.Oobabooga.Models;
using Discord.Net;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic
{
    public static class ClaudeMain
    {
        public static HttpClient hc = new HttpClient();
        public static CancellationToken ct;
        private const string chatHistoryDir = "data/claude/";

        public static void Exit()
        {
            
        }

        public static async Task<string> DownloadAttachment(string url, ulong threadId, string extension)
        {
            byte[] fileContents = await hc.GetByteArrayAsync(url);
            string fn = Guid.NewGuid().ToString("N");
            if(!Directory.Exists(chatHistoryDir + threadId))
                Directory.CreateDirectory(chatHistoryDir + threadId);
            string fullFilename = $"{chatHistoryDir}{threadId}/{fn}.{extension}";
            File.WriteAllBytes(fullFilename, fileContents);
            return fullFilename;
        }

        public static void Init(CancellationToken cancellationToken)
        {
            // do i need to do anything? its really a one-shot api
            ct = cancellationToken;
            if (!Directory.Exists(chatHistoryDir))
            {
                Directory.CreateDirectory(chatHistoryDir);
            }
            // set headers on the HttpClient for all requests
            hc.DefaultRequestHeaders.Add("x-api-key", SharedContext.Instance.GetConfig().Anthropic.ApiKey);
            hc.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }

        public static async Task<ClaudeChatRequest> FindExistingChat(ulong threadId)
        {
            if (File.Exists(chatHistoryDir + threadId + ".json"))
                return JsonConvert.DeserializeObject<ClaudeChatRequest>(File.ReadAllText(chatHistoryDir + threadId + ".json"));
            return null;
        }

        public static async Task DeleteChat(ulong threadId)
        {
            if (File.Exists(chatHistoryDir + threadId + ".json"))
                File.Delete(chatHistoryDir + threadId + ".json");
            if (Directory.Exists(chatHistoryDir + threadId))
                Directory.Delete(chatHistoryDir + threadId, true);
        }

        public static async Task<bool> StartConversation(ulong threadId, string presetName, uint? maxTokens = null, float? temp = null)
        {
            try
            {
                // create the new file
                ClaudeChatRequest cr = new ClaudeChatRequest();
                // this is where we'd get the profile
                if (SharedContext.Instance.GetConfig().Anthropic.Presets.ContainsKey(presetName))
                {
                    AnthropicPreset p = SharedContext.Instance.GetConfig().Anthropic.Presets[presetName];
                    if (p.SystemPrompt != null)
                        cr.system = p.SystemPrompt;
                    cr.max_tokens = p.MaxTokens;
                    cr.temperature = p.Temperature;
                    DiscordMain.SendThreadMessage(threadId, (p.Greeting != "" ? p.Greeting : "Conversation begins"), null, true);
                    File.WriteAllText(chatHistoryDir + threadId + ".json", JsonConvert.SerializeObject(cr, Formatting.Indented, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    }));
                    return true;
                }
                SharedContext.Instance.Log(LogLevel.ERR, "Anthropic", "Preset not found: " + presetName);
                return false;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Anthropic", $"Crashed when starting a new conversation: {ex.ToString()}");
                return false;
            }
        }

        public static async Task AddMessage(ulong threadId, string prompt, List<string> filenames)
        {
            try
            {
                // low effort
                string targetFileName = chatHistoryDir + threadId + ".json";
                ClaudeChatRequest? cr = null;
                if (File.Exists(targetFileName))
                {
                    SharedContext.Instance.Log(LogLevel.INFO, "Anthropic", $"File exists: {targetFileName}");
                    cr = JsonConvert.DeserializeObject<ClaudeChatRequest>(File.ReadAllText(targetFileName));
                }
                if (cr == null)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Anthropic", "Error reading conversation history: " + threadId);
                    return;
                }
                cr.messages.Add(new ClaudeMessage(prompt, filenames));
                File.WriteAllText(targetFileName, JsonConvert.SerializeObject(cr, Formatting.Indented, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                }));
                // make the request to claude
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Anthropic.URL + "/v1/messages", cr, new System.Text.Json.JsonSerializerOptions()
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                SharedContext.Instance.Log(LogLevel.INFO, "Anthropic", "Response code: " + response.StatusCode);
                ClaudeChatResponse res = await response.Content.ReadFromJsonAsync<ClaudeChatResponse>();
                if (res != null)
                {
                    // update and save the conversation
                    cr.messages.Add(new ClaudeMessage(res.role, res.content));
                    File.WriteAllText(targetFileName, JsonConvert.SerializeObject(cr, Formatting.Indented, new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    }));
                    // i cbf doing this streaming, so wait for the response and update the thread
                    // if > 2000 chars, split across multiple messages? attach a text file as normal?
                    DiscordMain.SendThreadMessage(threadId, res.content[0].text, null, true); // give a partial output + attach the file
                }
                else
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Anthropic", "Response was null: " + threadId);
                }
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Anthropic", $"Crashed when adding new message: {ex.ToString()}");
            }
        }
    }
}
