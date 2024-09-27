using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class LLMResponse
    {
        public LLMResponse() { }
        public LLMResponse(string user, string message, string finishReason, uint tokens) 
        {
            User = user;
            Message = message;
            TokenCount = tokens;
            FinishReason = Enum.Parse<OpenAIFinishReasons>(finishReason);
        }
        public LLMResponse(OpenAIChatResponse response)
        {
            if (response?.choices == null || response.choices.Count == 0)
                return;

            Choice c = response.choices.FirstOrDefault();
            if (c == null)
                return;

            Message = c.message.content;
            User = c.message.role;
            Id = response.id;
            TokenCount = response.usage.total_tokens;
            CompletionTokens = response.usage.completion_tokens;
            PromptTokens = response.usage.prompt_tokens;
            FinishReason = Enum.Parse<OpenAIFinishReasons>(c.finish_reason);
        }
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string User { get; set; } = "";
        public uint TokenCount { get; set; } = 0;
        public uint CompletionTokens { get; set; } = 0;
        public uint PromptTokens { get; set; } = 0;
        public OpenAIFinishReasons FinishReason { get; set; }
    }
}
