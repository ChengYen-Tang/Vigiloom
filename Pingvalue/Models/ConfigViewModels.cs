using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace Pingvalue.Models
{
    public class ConfigViewModels
    {
        [Display(Name = "LINE:Channel access token")]
        public string LineToken { get; set; }
        [Display(Name = "LINE:答覆訊息")]
        public string LineRetornMessage { get; set; }
        [Display(Name = "LINE:群組Token")]
        public string LineGroupToken { get; set; }
    }
}