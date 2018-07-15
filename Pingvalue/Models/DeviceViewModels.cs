using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace Pingvalue.Models
{
    public class  DeviceGroupViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }

        [Display(Name = "群組名稱")]
        public string GroupName { get; set; }

        [Display(Name = "建立時間")]
        public DateTime CreateTime { get; set; }
    }

    public class CreateDeviceGroupViewModel
    {
        [Display(Name = "群組名稱")]
        public string GroupName { get; set; }
    }

    public class EditDeviceGroupViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }

        [Display(Name = "群組名稱")]
        public string GroupName { get; set; }
    }

    public class DeviceViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }

        [Display(Name = "設備名稱")]
        public string DeviceName { get; set; }

        [Required]
        [StringLength(40)]
        [Display(Name = "IP位置")]
        public string IPAddress { get; set; }

        [Display(Name = "連線狀態")]
        public bool IsOnline { get; set; }

        [Display(Name = "建立時間")]
        public DateTime CreateTime { get; set; }
    }

    public class CreateDeviceViewModel
    {
        [Display(Name = "設備名稱")]
        public string DeviceName { get; set; }

        [Required]
        [StringLength(40)]
        [Display(Name = "IP位置")]
        public string IPAddress { get; set; }
    }

    public class EditeDeviceViewModel
    {
        [Key]
        [Required]
        public Guid Id { get; set; }

        [Display(Name = "設備名稱")]
        public string DeviceName { get; set; }

        [Required]
        [StringLength(40)]
        [Display(Name = "IP位置")]
        public string IPAddress { get; set; }

        public IEnumerable<SelectListItem> GroupList { get; set; }
    }

    public class DetailDeviceViewModel
    {
        public Guid Id { get; set; }
        public List<PingData> PingDatas { get; set; }
        public string DatetimePicker { get; set; }
        public string ChartTimeList { get; set; }
        public string CharDelayList { get; set; }
    }
}