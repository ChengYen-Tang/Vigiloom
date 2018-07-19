using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace Pingvalue.Models
{
    public class SpeedTestViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        public DateTime TestTime { get; set; }
        public string SpeedDownload { get; set; }
        public string SpeetUpload { get; set; }
    }

    public class IndexSpeedTestViewModel
    {
        public List<SpeedTestViewModel> SpeedTestDatas { get; set; }
        public string DatetimePicker { get; set; }
        public string ChartTimeList { get; set; }
        public string CharUploadList { get; set; }
        public string CharDownloadList { get; set; }
    }

    public class DetailSpeedTestViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        [Display(Name = "測試時間")]
        public DateTime TestTime { get; set; }
        [Display(Name = "網際網路服務供應商")]
        public string ISP { get; set; }
        public double ClientLongitude { get; set; }
        public double ClientLatitude { get; set; }
        [Display(Name = "測試伺服器")]
        public string Server { get; set; }
        public double ServerLongitude { get; set; }
        public double ServerLatitude { get; set; }
        [Display(Name = "延遲時間")]
        public long DelayTime { get; set; }
        [Display(Name = "下載速度")]
        public string SpeedDownload { get; set; }
        [Display(Name = "上傳速度")]
        public string SpeetUpload { get; set; }
    }
}