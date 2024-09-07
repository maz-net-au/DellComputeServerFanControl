﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.ComfyUI
{
    public class GenerationRequest
    {
        public string flowName { get; set; }
        public List<ComfyUIField> replacements { get; set; }
        public GenerationRequest() { } // for deserialising
        public GenerationRequest(string _flowName, List<ComfyUIField> _replacements) 
        {
            flowName = _flowName;
            replacements = _replacements;
        }
    }
}
