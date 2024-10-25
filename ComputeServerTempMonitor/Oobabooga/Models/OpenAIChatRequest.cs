﻿using ComputeServerTempMonitor.Common;
using Discord.Net;
using NvAPIWrapper.DRS.SettingValues;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class OpenAIChatRequest
    {
        public OpenAIChatRequest() { }
        public OpenAIChatRequest(ChatHistory ch)
        {
            if (OobaboogaMain.CurrentModel == "")
            {
                OobaboogaMain.CurrentModel = OobaboogaMain.GetLoadedModel().Result;
                if (OobaboogaMain.CurrentModel != "")
                {
                    truncation_length = Convert.ToUInt32(SharedContext.Instance.GetConfig().Oobabooga.Models[OobaboogaMain.CurrentModel]?.Args?["n_ctx"] ?? 8192); // 8k failsafe default?
                }
                else
                {
                    SharedContext.Instance.Log(LogLevel.ERR, "OpenAIChatRequest.ctor", "Cannot load current model.");
                    truncation_length = 8192;
                }
            }
            messages = ch.Messages;
            if (ch.Character != null && ch.Character != "")
            {
                character = ch.Character;
            }
            name2 = ch.Name;
            stop.Add($"{ch.Name}:");
            name1 = ch.Username;
            stop.Add($"{ch.Username}:");
            context = ch.SystemPrompt;
            preset = ch.Preset;
            // this isn't safe. probs move it. its a default anyway
            stream = SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.IsStreaming;
            mode = SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.Mode;
            max_tokens = SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.MaxNewTokens;
        }
        public List<OpenAIMessage> messages { get; set; } = new List<OpenAIMessage>();
        public string? character { get; set; }
        public string name1 { get; set; }
        public string name2 { get; set; }
        public List<string> stop { get; set; } = new List<string>();
        public uint max_tokens { get; set; } = 1024;
        public string mode { get; set; } = "chat";
        public uint truncation_length { get; set; } = 16384;
        public string preset { get; set; }
        public bool stream { get; set; }
        public bool continue_ { get; set; } = false;
        public string? context { get; set; }
    }

    public class OpenAIMessage
    {
        public OpenAIMessage(Roles role, string content, ulong msgId, uint token_count = 0)
        {
            this.role = Enum.GetName(role);
            this.content = content;
            this.tokens = token_count;
            this.msgId = msgId;
        }

        public string role { get; set; } = ""; // system, user or assistant?
        public string content { get; set; } = "";
        public uint tokens { get; set; } = 0;
        public ulong msgId { get; set; }
    }
}
