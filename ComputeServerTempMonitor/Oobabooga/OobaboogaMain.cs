using ComputeServerTempMonitor.Oobabooga.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor.Oobabooga
{
    public static class OobaboogaMain
    {
        public static HttpClient hc = new HttpClient();

        public static async Task<List<string>> GetModelList()
        {
            HttpResponseMessage result = await hc.GetAsync("http://192.168.1.100:5000/v1/internal/model/list");
            ModelList models = await result.Content.ReadFromJsonAsync<ModelList>();
            if (models != null)
            {
                Console.WriteLine(models);
                return models.model_names;
            }
            return new List<string>();
        }

        // http://192.168.1.100:5000/v1/internal/model/info basic stats about what model is loaded. nothing useful except to check its up


        public static async Task<string> RunArbitraryCommand(object post)
        {
            HttpResponseMessage response = await hc.PostAsJsonAsync("http://192.168.1.100:5000/v1/internal/completions", post);
            string body = await response.Content.ReadAsStringAsync();
            if (body != null)
            {
                Console.WriteLine(body);
            }
            return body;
        }


    }
}
