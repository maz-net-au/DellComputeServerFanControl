using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor
{
    public static class ComfyUI
    {
        // make all of these static functions. you should be able to call any of them from anywhere like an API wrapper with extra steps
        // not sure how to handle multiple comfyui instances. maybe list them all as URLs and round-robin?

        // get workflow names. takes ComfyUIConfig object
        // get vars for a workflow
        // generate workflow takes a workflow name and a map of var names and values
        // for each var in the workflow config, check if its set, if its set then try to place it into the flow
        // send workflow to API
        // this should be in a long running task so i can replace it (15 mins should be fine). pass in the handle to reply to
        // upon complete, attach image to message (somehow) and add buttons to run the generation again, and to upscale
        // do i allow multiple in a batch?
        //
    }
}
