namespace Pingvalue.Models
{
    using System;
    using System.Data.Entity;
    using System.ComponentModel.DataAnnotations.Schema;
    using System.Linq;

    public partial class PingvalueModels : DbContext
    {
        public PingvalueModels()
            : base("name=DefaultConnection")
        {
        }

        public virtual DbSet<DeviceGroup> DeviceGroups { get; set; }
        public virtual DbSet<Device> Devices { get; set; }
        public virtual DbSet<PingData> PingDatas { get; set; }
        public virtual DbSet<SpeedTest> SpeedTests { get; set; }


        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
        }
    }
}
