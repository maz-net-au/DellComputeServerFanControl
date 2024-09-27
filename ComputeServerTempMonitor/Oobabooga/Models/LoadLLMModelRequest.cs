using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public  class LoadLLMModelRequest
    {
        public string model_name { get; set; } = "";
        public Dictionary<string, object> args { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> settings { get; set; } = new Dictionary<string, object>();
    }
}
