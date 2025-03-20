using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Anthropic.Models
{
    public class ClaudeUsage
    {
        public uint input_tokens { get; set; }
        public uint output_tokens { get; set; }

        public double cost
        {
            get
            {
                return ((input_tokens / 1000000.0) * 3.0) + ((output_tokens / 1000000.0) * 15.0);
            }
        }
    }
}
