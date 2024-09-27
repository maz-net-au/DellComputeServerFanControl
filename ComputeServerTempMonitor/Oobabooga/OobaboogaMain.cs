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
                    await StopChat(id); // kill the current request
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


        //public static async Task<LLMResponse> StartChat(string character, ulong userId, string system, string prompt, ulong id) // do we allow settings? should i have preset support?
        //{
        //    if (userId == 0 || CurrentModel == "")
        //    {
        //        SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", "User or current model is blank");
        //        return null;
        //    }
        //    ChatHistory chatHistory = new ChatHistory();
        //    // let the discord module deal with making the thread etc. this one simply runs generation
        //    // i probably need to store a uuid for the chat, the thread Id, the guild/server, the user who started it, total context length
        //    OpenAIChatRequest request = new OpenAIChatRequest();
        //    request.model = CurrentModel;
        //    // where do i get the system prompt? do I have to do that manually?
        //    request.character = character;
        //    request.name2 = "You";
        //    if (system != "")
        //        request.messages.Add(new OpenAIMessage(Roles.system, system));
        //    request.messages.Add(new OpenAIMessage(Roles.user, prompt));
        //    request.truncation_length = 16000;
        //    HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions", request);
        //    OpenAIChatResponse res = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
        //    return new LLMResponse(res);
        //}

        public static async Task StopChat(ulong id) // does this return the message so far? or does that come out from the request above?
        {
            HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/internal/stop-generation", new object());
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

        public static async Task Replace(ulong id, ulong msgId, string replacementText) // replace the bot's last reply. or by ID
        {

        }

        public static async Task Update(ulong id, ulong msgId, string newText) // replace the your own last reply and regenerate
        {

        }

        public static async Task Regenerate(ulong id) // regenerate the bot's last reply
        {
            // trim the last "assistant" type message, and get the message before to send to reply?
        }



        public static async Task<LLMResponse> Ask(string prompt, string system) // do we allow settings? should i have preset support?
        {
            try
            {
                if (CurrentModel == "")
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", "Current model is blank");
                    return null;
                }
                // let the discord module deal with making the thread etc. this one simply runs generation
                // i probably need to store a uuid for the chat, the thread Id, the guild/server, the user who started it, total context length
                OpenAIChatRequest request = new OpenAIChatRequest();
                // where do i get the system prompt? do I have to do that manually?
                request.character = "BetterAssistant";
                request.name1 = "You";
                request.name2 = "BetterAssistant";
                request.max_tokens = (uint)(SharedContext.Instance.GetConfig().Oobabooga.DefaultParams?["max_new_tokens"] ?? 512);
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
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", ex.ToString());
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
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", ex.ToString());
            }
            return null;
        }
        public static async Task<LLMResponse> Continue(ulong id, string username)
        {
            if (!CurrentChats.ContainsKey(id))
            {
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", "Conversation not found.");
                return null;
            }
            if (CurrentChats[id].Username != username)
            {
                return null;
            }
            try
            {
                OpenAIChatRequest request = new OpenAIChatRequest(CurrentChats[id]);
                request.continue_ = true;
                CurrentChats[id].LastMessage = DateTime.Now;
                CurrentChats[id].IsGenerating = true;
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions", request);
                CurrentChats[id].IsGenerating = false;
                OpenAIChatResponse res = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
                LLMResponse llmr = new LLMResponse(res);
                if (CurrentChats.ContainsKey(id))
                {
                    CurrentChats[id].Messages[CurrentChats[id].Messages.Count-1] = new OpenAIMessage(Roles.assistant, llmr.Message, llmr.CompletionTokens); // does this replace?
                    CurrentChats[id].TokenCount = llmr.TokenCount; // await TokenCount(CurrentChats[id]);
                    await WriteHistory();
                }
                return llmr;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", ex.ToString());
            }
            return null;
        }
        public static async Task<LLMResponse> Reply(ulong id, ulong msgId, string message)
        {
            try
            {
                if (!CurrentChats.ContainsKey(id))
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", "Conversation not found.");
                    return null;
                }
                CurrentChats[id].Messages.Add(new OpenAIMessage(Roles.user, message, msgId));
                OpenAIChatRequest request = new OpenAIChatRequest(CurrentChats[id]);
                CurrentChats[id].LastMessage = DateTime.Now;
                CurrentChats[id].IsGenerating = true;
                //Console.WriteLine(JsonConvert.SerializeObject(request));
                HttpResponseMessage response = await hc.PostAsJsonAsync(SharedContext.Instance.GetConfig().Oobabooga.URL + "/v1/chat/completions", request);
                //Console.WriteLine();
                //Console.WriteLine(JsonConvert.SerializeObject(response));
                CurrentChats[id].IsGenerating = false;
                if (!response.IsSuccessStatusCode)
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "Oobabooga.Reply", $"Reveived status code: {response.StatusCode}");
                    return new LLMResponse();
                }
                OpenAIChatResponse res = await response.Content.ReadFromJsonAsync<OpenAIChatResponse>();
                LLMResponse llmr = new LLMResponse(res);
                if (CurrentChats.ContainsKey(id))
                {
                    CurrentChats[id].Messages.Add(new OpenAIMessage(Roles.assistant, llmr.Message, llmr.CompletionTokens));
                    CurrentChats[id].TokenCount = llmr.TokenCount; // await TokenCount(CurrentChats[id]);
                    await WriteHistory();
                }
                return llmr;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "OobaboogaMain", ex.ToString());
            }
            return null;
        }

    }
}
