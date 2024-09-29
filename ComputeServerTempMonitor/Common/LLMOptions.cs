using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Common
{
    public class LLMOptions
    {
        public uint MaxNewTokens { get; set; } = 1024;
        public string Mode { get; set; } = "chat";
        public bool IsStreaming { get; set; } = true;
        public uint StreamMessageInterval { get; set; } = 3000;
        public bool AllowOtherUsersParticipate { get; set; } = false;
        public bool AllowOtherUsersControl { get; set; } = false;
        public bool AllowMultipleConversations { get; set; } = false;
        public bool RemovePreviousControls { get; set; } = true;
    }
}
