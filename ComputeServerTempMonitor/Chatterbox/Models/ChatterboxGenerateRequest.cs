using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Chatterbox.Models
{
    public class ChatterboxGenerateRequest
    {
        public string text { get; set; } = "";
        public string audioPath { get; set; } // dunno if i need to manually encode this
        public double exaggeration { get; set; } = 0.5d;
        public double cfg_weight { get; set; } = 0.5d;
    }
}
