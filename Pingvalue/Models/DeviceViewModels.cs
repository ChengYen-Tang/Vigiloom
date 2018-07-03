using System;
using System.ComponentModel.DataAnnotations;

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
}