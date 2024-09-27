using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class OpenAIChatResponse
    {
        public List<Choice> choices { get; set; }
        public uint created { get; set; }
        public string id { get; set; }
        public string model { get; set; }
        public string @object { get; set; }
        public Usage usage { get; set; }
    }

    public class Choice
    {
        public string finish_reason { get; set; }
        public int index { get; set; }
        public Message message { get; set; }
        public object logprobs { get; set; }
    }

    public class Message
    {
        public string content { get; set; }
        public string role { get; set; }
    }

    public class Usage
    {
        public uint completion_tokens { get; set; }
        public uint prompt_tokens { get; set; }
        public uint total_tokens { get; set; }
    }
}
