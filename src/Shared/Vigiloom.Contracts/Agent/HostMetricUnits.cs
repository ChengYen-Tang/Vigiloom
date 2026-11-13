namespace Vigiloom.Contracts.Agent;

/// <summary>Display units for each supported host metric.</summary>
public static class HostMetricUnits
{
    public static string Name(AgentMetricKind kind) => kind switch
    {
        AgentMetricKind.CpuTotalPercent => "CPU 使用率", AgentMetricKind.CpuCorePercent => "CPU 核心使用率",
        AgentMetricKind.LoadAverage1 => "系統負載（1 分鐘）", AgentMetricKind.LoadAverage5 => "系統負載（5 分鐘）", AgentMetricKind.LoadAverage15 => "系統負載（15 分鐘）",
        AgentMetricKind.MemoryTotalBytes => "總記憶體", AgentMetricKind.MemoryAvailableBytes => "可用記憶體",
        AgentMetricKind.SwapTotalBytes => "交換空間總量", AgentMetricKind.SwapFreeBytes => "可用交換空間",
        AgentMetricKind.PageFileTotalBytes => "分頁檔總量", AgentMetricKind.PageFileUsedBytes => "已用分頁檔",
        AgentMetricKind.VolumeTotalBytes => "總磁碟空間", AgentMetricKind.VolumeAvailableBytes => "可用磁碟空間",
        AgentMetricKind.DiskReadBytesPerSecond => "磁碟讀取", AgentMetricKind.DiskWriteBytesPerSecond => "磁碟寫入",
        AgentMetricKind.DiskReadLatencyMilliseconds => "磁碟讀取延遲", AgentMetricKind.DiskWriteLatencyMilliseconds => "磁碟寫入延遲",
        AgentMetricKind.NetworkReceivedBytesPerSecond => "網路接收", AgentMetricKind.NetworkSentBytesPerSecond => "網路傳送",
        AgentMetricKind.NetworkReceivedErrorsPerSecond => "網路接收錯誤", AgentMetricKind.NetworkSentErrorsPerSecond => "網路傳送錯誤",
        AgentMetricKind.ProcessCpuPercent => "程序 CPU 使用率", AgentMetricKind.ProcessWorkingSetBytes => "程序工作集記憶體",
        _ => "未知指標"
    };

    public static string For(AgentMetricKind kind) => kind switch
    {
        AgentMetricKind.CpuTotalPercent or AgentMetricKind.CpuCorePercent or AgentMetricKind.ProcessCpuPercent => "%",
        AgentMetricKind.LoadAverage1 or AgentMetricKind.LoadAverage5 or AgentMetricKind.LoadAverage15 => "負載",
        AgentMetricKind.DiskReadLatencyMilliseconds or AgentMetricKind.DiskWriteLatencyMilliseconds => "ms",
        AgentMetricKind.NetworkReceivedErrorsPerSecond or AgentMetricKind.NetworkSentErrorsPerSecond => "errors/s",
        AgentMetricKind.DiskReadBytesPerSecond or AgentMetricKind.DiskWriteBytesPerSecond or
            AgentMetricKind.NetworkReceivedBytesPerSecond or AgentMetricKind.NetworkSentBytesPerSecond => "bytes/s",
        AgentMetricKind.MemoryTotalBytes or AgentMetricKind.MemoryAvailableBytes or AgentMetricKind.SwapTotalBytes or AgentMetricKind.SwapFreeBytes or
            AgentMetricKind.PageFileTotalBytes or AgentMetricKind.PageFileUsedBytes or AgentMetricKind.VolumeTotalBytes or
            AgentMetricKind.VolumeAvailableBytes or AgentMetricKind.ProcessWorkingSetBytes => "bytes",
        _ => ""
    };
}
