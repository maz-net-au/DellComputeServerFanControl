using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Zonos.Models
{
    public class ZonosGenerateRequest
    {
        public string text { get; set; } = "";
        public string speaker_audio { get; set; } // dunno if i need to manually encode this
        public int speaking_rate { get; set; } = 15;
        public string language_iso_code { get; set; } = "en-us";
        public string mime_type { get; set; } = "audio/mp3";
    }
}
