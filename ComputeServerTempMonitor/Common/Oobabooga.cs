using ComputeServerTempMonitor.Oobabooga.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Common
{
    public class OobaboogaConfig
    {
        public string URL { get; set; } = "";
        public LLMPathsConfig Paths { get; set; } = new LLMPathsConfig();
        public LLMOptions DefaultParams { get; set; } = new LLMOptions();
        public Dictionary<string, CharacterSettings> DisplayCharacters { get; set; } = new Dictionary<string, CharacterSettings>();
        public Dictionary<string, ModelConfig> Models { get; set; } = new Dictionary<string, ModelConfig>();
    }
}
