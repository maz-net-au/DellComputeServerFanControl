using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class OpenAIModelResponse
    {
        public string model_name { get; set; } = "";
        public string[] lora_names { get; set; } = new string[0];
        public string loader { get; set; } = "";
    }
}
