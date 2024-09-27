using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public class ModelConfig
    {
        public string DisplayName { get; set; } = "";
        public string BaseModel { get; set; } = "";
        public string Size { get; set; } = "";
        public string FileType { get; set; } = "";
        public string Loader { get; set; } = "";
        public string Filename { get; set; } = "";
        public string Quantisation { get; set; } = "";
        public Dictionary<string, object> Params { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Args { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }
}
