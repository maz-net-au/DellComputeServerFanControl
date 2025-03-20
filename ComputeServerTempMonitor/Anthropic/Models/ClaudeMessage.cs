using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeMessage
    {
        public ClaudeMessage() { }
        public ClaudeMessage(string prompt, List<string> paths) 
        {
            role = "user"; // because this is us making a request
            foreach (string path in paths)
            {
                if (path != null && path != "")
                {
                    content.Add(new ClaudeContent(path));
                }
            }
            if (prompt != null && prompt != "")
            {
                ClaudeContent cc = new ClaudeContent();
                cc.type = "text";
                cc.text = prompt;
                content.Add(cc);
            }
        }
        public ClaudeMessage(string role, List<ClaudeContent> content)
        {
            this.role = role;
            this.content = content;
        }

        public string role { get; set; }
        public List<ClaudeContent> content { get; set; } = new List<ClaudeContent>();
    }
}
