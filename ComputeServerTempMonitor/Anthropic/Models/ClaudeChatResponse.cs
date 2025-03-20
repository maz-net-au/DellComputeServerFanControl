using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeChatResponse
    {
        public string id { get; set; }
        public List<ClaudeContent> content { get; set; }
        public string model { get; set; }
        public string role { get; set; }
        public string stop_reason { get; set; }
        public string stop_sequence { get; set; }
        public string type { get; set; } // probably an enum. nope. just "messages"
        public ClaudeUsage usage { get; set; }
    }    
}
