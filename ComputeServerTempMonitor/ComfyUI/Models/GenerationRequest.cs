using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ComputeServerTempMonitor.Common;

namespace ComputeServerTempMonitor.ComfyUI.Models
{
    public class GenerationRequest
    {
        public string flowName { get; set; }
        public List<ComfyUIField> replacements { get; set; }
        public FlowModelTypes type { get; set; }
        public GenerationRequest() { } // for deserialising
        public GenerationRequest(string _flowName, FlowModelTypes _type, List<ComfyUIField> _replacements)
        {
            flowName = _flowName;
            replacements = _replacements;
            type = _type;
        }
    }
}
