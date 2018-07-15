using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace Pingvalue.Models
{
    public class HomeViewModels
    {
        public Guid Id { get; set; }
        [Display(Name = "設備名稱")]
        public string DeviceName { get; set; }
        [Display(Name = "IP位置")]
        public string IPAddress { get; set; }
        [Display(Name = "連線狀態")]
        public bool IsOnline { get; set; }
    }
}