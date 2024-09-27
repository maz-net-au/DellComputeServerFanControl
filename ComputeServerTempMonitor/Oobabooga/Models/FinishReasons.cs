using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga.Models
{
    public enum OpenAIFinishReasons
    {
        stop,
        length,
        function_call,
        content_filter
    }
}
