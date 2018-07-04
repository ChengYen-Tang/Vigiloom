using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Pingvalue.Models
{
    public partial class DeviceGroup
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        public string GroupName { get; set; }
        public DateTime CreateTime { get; set; }

        public DeviceGroup()
        {
            this.Id = Guid.NewGuid();
            this.CreateTime = DateTime.Now;

            this.Devices = new List<Device>();
        }

        public virtual ICollection<Device> Devices { get; set; }
    }

    public partial class Device
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        public string DeviceName { get; set; }
        [Required]
        [StringLength(40)]
        public string IPAddress { get; set; }
        [Required]
        public bool IsOnline { get; set; }
        public DateTime CreateTime { get; set; }

        public Device()
        {
            this.Id = new Guid();
            this.CreateTime = DateTime.Now;

            this.DeviceGroups = new List<DeviceGroup>();
            this.pingDatas = new List<PingData>();
        }

        public virtual ICollection<DeviceGroup> DeviceGroups { get; set; }
        public virtual ICollection<PingData> pingDatas { get; set; }
    }

    public partial class PingData
    {
        [Key]
        [Required]
        public Guid Id { get; set; }
        public DateTime CreateTime { get; set; }
        public long Delay1 { get; set; }
        public long Delay2 { get; set; }
        public long Delay3 { get; set; }
        public long Delay4 { get; set; }

        public PingData()
        {
            this.Id = Guid.NewGuid();
            this.CreateTime = DateTime.Now;
        }

        public virtual Device Device { get; set; }
    }
}