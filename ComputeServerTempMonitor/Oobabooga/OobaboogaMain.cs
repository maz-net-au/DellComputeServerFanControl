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
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga
{
    // config option to allow more than one person to be part of a convo. (need to @ the bot?)
    // need a button and a permission to allow editing of bot responses. I'll build the context off the thread
    // allow setting the system prompt in the first message / chat start call
    public static class OobaboogaMain
    {
        public static HttpClient hc = new HttpClient();
        public static string CurrentModel = "";
        public static uint CurrentMaxContext = 0;
        public static CancellationToken cancellationToken;
        private const string chatHistoryFile = "data/llmHistory.json";
        public static Dictionary<ulong, ChatHistory> CurrentChats { get; set; } = new Dictionary<ulong, ChatHistory>();

        public static async Task WriteHistory()
        {
            File.WriteAllText(chatHistoryFile, JsonConvert.SerializeObject(CurrentChats, Formatting.Indented));
        }
        public static async Task LoadHistory()
        {
            if (File.Exists(chatHistoryFile))
            {
                CurrentChats = JsonConvert.DeserializeObject<Dictionary<ulong, ChatHistory>>(File.ReadAllText(chatHistoryFile));
                SharedContext.Instance.Log(LogLevel.INFO, "OobaboogaMain", "Chat history loaded.");
            }
        }

        public static List<string> GetModelList()
        {
            return SharedContext.Instance.GetConfig().Oobabooga.Models.Keys.ToList();

            //HttpResponseMessage result = await hc.GetAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/model/list");
            //ModelList models = await result.Content.ReadFromJsonAsync<ModelList>();
            //if (models != null)
            //{
            //    //Console.WriteLine(models);
            //    return models.model_names;
            //}
            //return new List<string>();
        }

        // http://192.168.1.100:5000/v1/internal/model/info basic stats about what model is loaded. nothing useful except to check its up
        public static async void Init(CancellationToken token)
        {
            hc.Timeout = new TimeSpan(0, 30, 0);
            cancellationToken = token;
            // get the current model
            CurrentModel = await GetLoadedModel();
            if (CurrentModel != "")
                CurrentMaxContext = Convert.ToUInt32(SharedContext.Instance.GetConfig().Oobabooga.Models[CurrentModel]?.Args?["n_ctx"] ?? 0);
            await LoadHistory();
        }

        public static void Exit()
        {
            WriteHistory();
        }

        public static async Task<string> GetLoadedModel()
        {
            try
            {
                OpenAIModelResponse result = await hc.GetFromJsonAsync<OpenAIModelResponse>(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/model/info");
                string mn = result?.model_name ?? "";
                foreach (KeyValuePair<string, ModelConfig> mc in SharedContext.Instance.GetConfig().Oobabooga.Models)
                {
                    if (mn == mc.Value.Filename)
                        return mc.Key;
                }
                return "";
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", ex.ToString());
                return "";
            }
            // this is an object?
        }

        public static async Task LoadModel(string model)
        {
            if (SharedContext.Instance.GetConfig().Oobabooga.Models.ContainsKey(model))
            {
                // load the model. take its args and settings. should i let someone override this?
                LoadLLMModelRequest lmr = new LoadLLMModelRequest();
                lmr.model_name = SharedContext.Instance.GetConfig().Oobabooga.Models[model].Filename;
                lmr.args = SharedContext.Instance.GetConfig().Oobabooga.Models[model].Args;
                lmr.settings = SharedContext.Instance.GetConfig().Oobabooga.Models[model].Settings;
                try
                {
                    HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/model/load", lmr);
                    // the response is a string? log it to see what it is
                    if (response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        // model loaded?
                        SharedContext.Instance.Log(LogLevel.INFO, "Oobabooga.LoadModel", $"Model loaded successfully: {model}, {body}");
                        CurrentModel = lmr.model_name;
                        return;
                    }
                    SharedContext.Instance.Log(LogLevel.INFO, "Oobabooga.LoadModel", $"Model '{model}' failed to load: {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    SharedContext.Instance.Log(LogLevel.INFO, "Oobabooga.LoadModel", $"Loading model '{model}' threw an exception: {ex.ToString()}");
                }
            }
        }

        public static async Task DeleteChat(ulong id)
        {
            if (CurrentChats.ContainsKey(id))
            {
                if (CurrentChats[id].IsGenerating)
                {
                    await Stop(id, CurrentChats[id].Username); // kill the current request
                }
                CurrentChats.Remove(id);
                WriteHistory();
            }
        }

        public static ChatHistory FindExistingChat(string? username = null, ulong? threadId = null)
        {
            foreach (var chat in CurrentChats)
            {
                if ((username == null || chat.Value.Username == username) && (!threadId.HasValue || chat.Value.ThreadId == threadId.Value))
                {
                    return chat.Value;
                }
            }
            return null;
        }

        public static async Task<uint> GetTokenCount(string msg)
        {
            HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/token-count", new Dictionary<string, string>
            {
                { "text", msg }
            });
            if (!response.IsSuccessStatusCode)
                return 0;
            Dictionary<string, uint> result = await response.Content.ReadFromJsonAsync<Dictionary<string, uint>>();
            return result["length"];
        }


        public static async Task<uint> TokenCount(ChatHistory ch)
        {
            uint total = 0;
            foreach (OpenAIMessage msg in ch.Messages)
            {

                if (msg.tokens == 0 && msg.content != "")
                {
                    // get the count, store the count
                    msg.tokens = await GetTokenCount(msg.content);
                }
                total += msg.tokens;
            }
            return total;
        }

        public static async Task<LLMResponse> Ask(string prompt, string system) // do we allow settings? should i have preset support?
        {
            try
            {
                if (CurrentModel == "")
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Ask", "Current model is blank");
                    return null;
                }
                // let the discord module deal with making the thread etc. this one simply runs generation
                // i probably need to store a uuid for the chat, the thread Id, the guild/server, the user who started it, total context length
                OpenAIChatRequest request = new OpenAIChatRequest();
                // where do i get the system prompt? do I have to do that manually?
                request.character = "BetterAssistant";
                request.name1 = "You";
                request.name2 = "BetterAssistant";
                request.max_tokens = SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.MaxNewTokens;
                if (system != "")
                {
                    request.context = system;
                    //request.messages.Add(new OpenAIMessage(Roles.system, system));
                }
                request.messages.Add(new OpenAIMessage(Roles.user, prompt, 0));
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions", request);
                OpenAIChatResponse res = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
                return new LLMResponse(res);
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Ask", ex.ToString());
            }
            return new LLMResponse();
        }

        public static async Task<ChatHistory> InitChat(string username, string character, string systemPrompt, ulong id, ulong ownerId)
        {
            try
            {
                ChatHistory chatHistory = new ChatHistory();
                chatHistory.ThreadId = id;
                chatHistory.ServerId = ownerId;
                chatHistory.Username = username;
                chatHistory.SystemPrompt = systemPrompt;
                chatHistory.LastMessage = DateTime.Now;

                // this creates a new ChatHistory with just the system prompt set, and send back the Greeting message from the character
                // The user has selected from our display names and passed through the internal name
                CharacterSettings ourChar = SharedContext.Instance.GetConfig().Oobabooga.DisplayCharacters[character];
                chatHistory.Name = ourChar.DisplayName;
                chatHistory.Preset = ourChar.Preset;

                // see if we can load some data from a character file
                string characterPath = $"{SharedContext.Instance.GetConfig().Oobabooga.Paths.Characters}{character}.yaml";
                if (File.Exists(characterPath))
                {
                    string characterFile = File.ReadAllText(characterPath);
                    YamlDotNet.Serialization.Deserializer deserialiser = new YamlDotNet.Serialization.Deserializer();
                    Character c = deserialiser.Deserialize<Character>(characterFile);
                    chatHistory.SystemPrompt = c.context;
                    chatHistory.Character = character;
                    chatHistory.Greeting = c.greeting;
                }
                CurrentChats.Add(id, chatHistory);
                await WriteHistory();
                return chatHistory;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Init", ex.ToString());
            }
            return null;
        }
        public static async Task Continue(ulong id, ulong msgId, string username)
        {
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Continue", "Conversation not found.");
                return;
            }
            if (CurrentChats[id].Username != username && !SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl)
            {
                return;
            }
            try
            {
                OpenAIChatRequest request = new OpenAIChatRequest(CurrentChats[id]);
                request.continue_ = true;
                CurrentChats[id].LastMessage = DateTime.Now;
                await Infer(request, id, msgId);

                return;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Continue", ex.ToString());
            }
            return;
        }

        public static async Task<bool> Reply(ulong id, ulong msgId, string message)
        {
            try
            {
                if (!CurrentChats.ContainsKey(id))
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Reply", "Conversation not found.");
                    return false;
                }
                CurrentChats[id].Messages.Add(new OpenAIMessage(Roles.user, message, msgId));
                CurrentChats[id].LastMessage = DateTime.Now;
                // make a new request to send
                OpenAIChatRequest request = new OpenAIChatRequest(CurrentChats[id]);
                await Infer(request, id, null);
                return true;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Reply", ex.ToString());
            }
            return false;
        }

        public static async Task Replace(ulong id, ulong msgId, string replacementText)
        {
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Replace", "Conversation not found.");
                return;
            }
            OpenAIMessage oim = CurrentChats[id].Messages.FirstOrDefault(x => x.msgId == msgId);
            if (oim != null)
            {
                oim.content = replacementText;
                oim.tokens = 0;
                await WriteHistory();
            }
            else
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Replace", "Message not found.");
            }
        }

        public static async Task DeleteMessage(ulong id, ulong msgId)
        {
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Delete", "Conversation not found.");
                return;
            }
            OpenAIMessage oim = CurrentChats[id].Messages.FirstOrDefault(x => x.msgId == msgId);
            if (oim != null)
            {
                CurrentChats[id].Messages.Remove(oim);
                await WriteHistory();
            }
            else
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Delete", "Message not found.");
            }
        }

        public static async Task Regenerate(ulong id, ulong msgId, string username) // regenerate the specified message
        {
            // find the chat
            // find the message
            // if the message isn't the last, carve the rest off and store
            // trigger a generation for the missing message, updating by ID
            // once finished, put the rest of the messages back
            // write cache
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Regenerate", "Conversation not found.");
                return;
            }
            List<OpenAIMessage> laterMessages = new List<OpenAIMessage>();
            try
            {
                laterMessages = await Trim(id, msgId);
                if (laterMessages == null)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Regenerate", $"Message not found. {msgId}");
                    return;
                }
                // remove the one we're replacing as well
                CurrentChats[id].Messages.RemoveAt(CurrentChats[id].Messages.Count - 1);
                OpenAIChatRequest request = new OpenAIChatRequest(CurrentChats[id]);
                // pass in the ID to update the existing message
                await Infer(request, id, msgId);
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Regenerate", ex.ToString());
            }
            finally
            {
                // put the removed messages back
                if (laterMessages != null)
                {
                    laterMessages.Reverse();
                    CurrentChats[id].Messages = CurrentChats[id].Messages.Concat(laterMessages).ToList();
                }
            }
        }

        public static async Task Stop(ulong id, string username) // does this return the message so far? or does that come out from the request above?
        {
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Stop", "Conversation not found.");
                return;
            }
            if (CurrentChats[id].Username != username && !SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl)
            {
                // only let the user that started the chat kill it
                return;
            }
            if (CurrentChats[id].IsGenerating)
            {
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/stop-generation", new object());
                Console.WriteLine(response.Content.ToString());
            }
        }

        private static async Task<List<OpenAIMessage>> Trim(ulong id, ulong msgId)
        {
            List<OpenAIMessage> laterMessages = new List<OpenAIMessage>();
            int pos = -1;
            try
            {
                for (int i = CurrentChats[id].Messages.Count - 1; i >= 0; i--)
                {
                    if (CurrentChats[id].Messages[i].msgId == msgId)
                    {
                        pos = i;
                        break; // found the one we want
                    }
                    laterMessages.Add(CurrentChats[id].Messages[i]); // these are coming out in reverse order
                    CurrentChats[id].Messages.RemoveAt(i);
                }
                if (pos == -1)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Trim", "Message not found.");
                    laterMessages.Reverse();
                    CurrentChats[id].Messages = CurrentChats[id].Messages.Concat(laterMessages).ToList();
                    return null;
                }
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Trim", ex.ToString());
                laterMessages.Reverse();
                CurrentChats[id].Messages = CurrentChats[id].Messages.Concat(laterMessages).ToList();
                return null;
            }
            return laterMessages;
        }

        private static async Task Infer(OpenAIChatRequest request, ulong id, ulong? existingMsgId)
        {
            OpenAIChatResponse resp = null;
            // let the system know we're the one generating currently
            CurrentChats[id].IsGenerating = true;
            //ulong? messageId = existingMsgId;
            StringBuilder totalMessage = new StringBuilder();
            // if existingMsgId exists in our cache, then it's content is the start of the reply
            OpenAIMessage oim = CurrentChats[id].Messages.FirstOrDefault(x => x.msgId == existingMsgId);
            if (oim != null)
            {
                // the existing message for a "continue" all comes back with the first token.
                // therefore don't add it or it duplicates it
                //totalMessage.Append(oim.content);
            }
            if (request.stream)
            {
                HttpRequestMessage hrm = new HttpRequestMessage(HttpMethod.Post, SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions");
                hrm.Content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
                HttpResponseMessage response = await hc.SendAsync(hrm, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                uint interval = SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.StreamMessageInterval;
                using (Stream s = await response.Content.ReadAsStreamAsync(cancellationToken))
                {
                    using (StreamReader sr = new StreamReader(s))
                    {
                        long lastTime = 0;
                        while (true)
                        {
                            string line = sr.ReadLine();
                            if (line == null || line == "")
                                continue;
                            if (line.StartsWith("data: "))
                            {
                                //Console.WriteLine(line);
                                line = line.Substring(6);
                                resp = JsonConvert.DeserializeObject<OpenAIChatResponse>(line);
                                if (resp != null && resp.choices.Count > 0)
                                {
                                    totalMessage.Append(resp.choices[0].delta.content);
                                    long now = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                                    if (now > (lastTime + interval))
                                    {
                                        // fire an update!
                                        existingMsgId = await DiscordMain.SendThreadMessage(id, totalMessage.ToString(), existingMsgId);
                                        lastTime = now;
                                    }
                                    if (resp.choices[0].finish_reason != null)
                                    {
                                        // this is the last part
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                existingMsgId = await DiscordMain.SendThreadMessage(id, totalMessage.ToString(), existingMsgId, true);
                DiscordMain.AddLLMUsage(CurrentChats[id].Username, resp.usage.prompt_tokens, resp.usage.completion_tokens); 
                CurrentChats[id].IsGenerating = false;
            }
            else
            {
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions", request);
                if (!response.IsSuccessStatusCode)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Infer", $"Reveived status code: {response.StatusCode}");
                    return;// new LLMResponse();
                }
                resp = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
                //LLMResponse llmr = new LLMResponse(res);
                if (resp != null && resp.choices.Count > 0)
                {
                    totalMessage.Append(resp.choices[0].message.content);
                    DiscordMain.AddLLMUsage(CurrentChats[id].Username, resp.usage.prompt_tokens, resp.usage.completion_tokens);
                }
                existingMsgId = await DiscordMain.SendThreadMessage(id, totalMessage.ToString(), null, true);
                CurrentChats[id].IsGenerating = false;
            }
            if (CurrentChats.ContainsKey(id) && resp != null)
            {
                // if this message is already here, replace it?
                if (oim == null)
                {
                    CurrentChats[id].Messages.Add(new OpenAIMessage(Roles.assistant, totalMessage.ToString(), existingMsgId.Value, resp.usage.completion_tokens));
                }
                else
                {
                    oim.content = totalMessage.ToString();
                    oim.tokens += resp.usage.completion_tokens; // replace needs to explicitly clear this first
                }
                CurrentChats[id].TokenCount = resp.usage.total_tokens;
                await WriteHistory();
            }
            return;
        }
    }
}
