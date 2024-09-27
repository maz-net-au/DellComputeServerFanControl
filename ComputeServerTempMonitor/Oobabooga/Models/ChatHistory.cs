using ComputeServerTempMonitor.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class ChatHistory
    {
        // discord metadata
        public ulong ThreadId { get; set; }
        public ulong ServerId { get; set; }

        // character data
        public string Character { get; set; } = null;
        public string Name { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public string Greeting { get; set; } = "";

        public string Username { get; set; } = "";
        //public string Model { get; set; } = ""; // not relevant
        public List<OpenAIMessage> Messages { get; set; } = new List<OpenAIMessage> { };
        //public int ContextLength { get; set; }
        public DateTime LastMessage { get; set; }
        public DateTime ChatStarted { get; set; } = DateTime.Now;
        public uint TokenCount { get; set; } = 0;
        public bool IsGenerating { get; set; } = false;
        public string Preset { get; set; }

        //public string ReplaceTokens()
        //{
        //    return SystemPrompt.Replace("{{char}}", Character).Replace("{{user}}", Username);
        //}
    }
}
