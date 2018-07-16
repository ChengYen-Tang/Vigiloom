using System;
using System.ComponentModel.DataAnnotations;

namespace Pingvalue.Models
{
    public class SpeedTest
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        public DateTime TestTime { get; set; }
        public string ISP { get; set; }
        public double ClientLongitude { get; set; }
        public double ClientLatitude { get; set; }
        public string Server { get; set; }
        public double ServerLongitude { get; set; }
        public double ServerLatitude { get; set; }
        public long DelayTime { get; set; }
        public string SpeedDownload { get; set; }
        public string SpeetUpload { get; set; }
    }
}