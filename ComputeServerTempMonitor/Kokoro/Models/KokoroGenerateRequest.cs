using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Kokoro.Models
{
    public class KokoroGenerateRequest
    {
        public string text { get; set; } = "";
        public string voice { get; set; } = "af_heart";
        public double speed { get; set; } = 1.0d;
        public string lang_code { get; set; } = "a";
    }
}
