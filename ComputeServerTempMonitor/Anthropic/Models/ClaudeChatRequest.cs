using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeChatRequest
    {
        public string model { get; set; } = "claude-3-7-sonnet-20250219";
        public uint max_tokens { get; set; } = 2000;
        public string? system { get; set; } = null;
        public List<ClaudeMessage> messages { get; set; } = new List<ClaudeMessage>();
        public float temperature { get; set; } = 1.0f;
    }
}
