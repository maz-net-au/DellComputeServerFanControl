using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI
{
    public class PromptRequest
    {
        public PromptRequest(Dictionary<string, Step> _prompt)
        {
            prompt = _prompt;
            client_id = Guid.NewGuid().ToString();
        }

        public Dictionary<string, Step> prompt { get; set; }
        public string client_id { get; set; }
    }
}
