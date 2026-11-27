using Microsoft.Extensions.Localization;
using Vigiloom.Web.Components.Account;

namespace Vigiloom.Web.Authentication;

internal static class AccountOperationMessages
{
    public static string DescribeValidation(AccountOperationResult result, IStringLocalizer<SetupText> text) =>
        string.Join(" ", result.Errors.Select((message, index) =>
        {
            if (result.ErrorCodes is not { } codes || index >= codes.Count) return message;
            var localized = text[$"Identity.{codes[index]}"];
            return localized.ResourceNotFound ? message : localized.Value;
        }));

    public static string Describe(AccountOperationResult result) => result.Status switch
    {
        AccountOperationStatus.InvalidRole => "請選擇有效的角色。",
        AccountOperationStatus.NotFound => "找不到此帳號。",
        AccountOperationStatus.LastAdministrator => "至少必須保留一位可使用的管理員。",
        AccountOperationStatus.SelfDeletion => "無法刪除目前登入的帳號。",
        AccountOperationStatus.SelfTwoFactorReset => "請至自己的雙重驗證設定頁停用。",
        AccountOperationStatus.InvalidCredentials => "管理員密碼不正確。",
        AccountOperationStatus.ValidationFailed => string.Join(" ", result.Errors),
        _ => "無法完成操作，請稍後重試。"
    };
}
