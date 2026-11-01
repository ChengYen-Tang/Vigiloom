namespace Vigiloom.Web.Authentication;

internal static class AccountOperationMessages
{
    public static string Describe(AccountOperationResult result) => result.Status switch
    {
        AccountOperationStatus.InvalidRole => "請選擇有效的角色。",
        AccountOperationStatus.NotFound => "找不到此帳號。",
        AccountOperationStatus.LastAdministrator => "至少必須保留一位可使用的管理員。",
        AccountOperationStatus.SelfDeletion => "無法刪除目前登入的帳號。",
        AccountOperationStatus.ValidationFailed => string.Join(" ", result.Errors),
        _ => "無法完成操作，請稍後重試。"
    };
}
