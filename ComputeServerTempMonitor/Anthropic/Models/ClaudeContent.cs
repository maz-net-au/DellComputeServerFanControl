using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeContent
    {
        public ClaudeContent() { }
        public ClaudeContent(string path) { 
            source = new ClaudeContentSource(path);
            string ext = Path.GetExtension(path).ToLower().Trim('.');
            if (ext == "pdf")
            {
                type = "document"; // apparnetly this works
            }
            // cache after an image / doc
            cache_control = new Dictionary<string, string>(){
                { "type", "ephemeral" }
            };
        }
        public string type { get; set; } = "image";
        public string text { get; set; }
        public Dictionary<string, string> cache_control { get; set; }
        public ClaudeContentSource source { get; set; }
    }
}
