using Vigiloom.Contracts.SpeedTests;
namespace Vigiloom.Web.Client.Components.SpeedTests;
public static class SpeedPresentation
{
    public static string State(SpeedTestState state) => state switch
    {
        SpeedTestState.Queued => "等待執行", SpeedTestState.Running => "測試中", SpeedTestState.Success => "完成",
        SpeedTestState.Partial => "部分結果", SpeedTestState.BudgetExceeded => "達流量上限", SpeedTestState.TimedOut => "逾時",
        SpeedTestState.Cancelled => "已取消", SpeedTestState.Interrupted => "執行中斷", SpeedTestState.NoServer => "無可用服務端",
        SpeedTestState.Unavailable => "測速工具不可用", _ => "失敗"
    };
    public static string Value(double? value, string unit) => value is { } number ? $"{number:0.###} {unit}" : "—";
}
