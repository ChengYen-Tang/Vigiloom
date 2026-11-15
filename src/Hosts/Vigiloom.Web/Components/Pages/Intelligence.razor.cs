using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

namespace Vigiloom.Web.Components.Pages;

public partial class Intelligence
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "source")] private Guid? SourceId { get; set; }
    [SupplyParameterFromQuery(Name = "sourcePage")] private int? SourcePage { get; set; }
    [SupplyParameterFromQuery(Name = "versionPage")] private int? VersionPage { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "rulesRevision")] private Guid? RulesRevision { get; set; }
    [SupplyParameterFromQuery(Name = "rulePage")] private int? RulePage { get; set; }
    [SupplyParameterFromQuery(Name = "iocPath")] private string? IocPath { get; set; }
    [SupplyParameterFromQuery(Name = "evidencePath")] private string? EvidencePath { get; set; }
    [SupplyParameterFromQuery(Name = "evidenceLine")] private int? EvidenceLine { get; set; }
    [SupplyParameterFromQuery(Name = "evidencePage")] private int? EvidencePage { get; set; }
    [SupplyParameterFromQuery(Name = "issuePage")] private int? IssuePage { get; set; }
    [SupplyParameterFromQuery(Name = "import")] private string? ImportResult { get; set; }
    [SupplyParameterFromForm(FormName = "intelligence-policy")] private PolicyForm PolicyInput { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "intelligence-activation")] private ActivationForm ActivationInput { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "intelligence-credential")] private CredentialForm CredentialInput { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "intelligence-recovery")] private RecoveryForm RecoveryInput { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "intelligence-revocation")] private RevocationForm RevocationInput { get; set; } = default!;
    private IntelligenceSourcePage? sources;
    private IntelligenceSourceView? selected;
    private IntelligenceRevisionPage? revisions;
    private AdvisoryImportProgressView? progress;
    private SigmaRulePage? sigmaRules;
    private SignatureFilePage? signatureFiles;
    private IocIssuePage? iocIssues;
    private SignatureEvidenceView? signatureEvidence;
    private Guid? shownRulesRevision;
    private string? error;
    private string? message;
    private bool policyOpen;

    protected override async Task OnInitializedAsync()
    {
        var submittedPolicy = PolicyInput is not null;
        var submittedActivation = ActivationInput is not null;
        var submittedCredential = CredentialInput is not null;
        var submittedRecovery = RecoveryInput is not null;
        RevocationInput ??= new(); PolicyInput ??= new(); ActivationInput ??= new(); CredentialInput ??= new(); RecoveryInput ??= new();
        await LoadAsync();
        if (selected is null) return;
        if (RevocationInput.SourceId == Guid.Empty) RevocationInput.SourceId = selected.Id;
        if (!submittedPolicy) PolicyInput = new() { SourceId = selected.Id, Version = selected.Version, Enabled = selected.Enabled,
            Interval = selected.UpdateIntervalSeconds, Stale = selected.StaleAfterSeconds, MiB = selected.MaximumBytes / 1048576m, Records = selected.MaximumRecords };
        if (!submittedActivation) ActivationInput = new() { SourceId = selected.Id, ExpectedActiveId = selected.ActiveRevisionId, ExpectedVersion = selected.ActivationVersion };
        if (!submittedCredential) CredentialInput = new() { SourceId = selected.Id, Version = selected.Version };
        if (!submittedRecovery && progress?.ImportId is { } id) RecoveryInput = new() { SourceId = selected.Id, ImportId = id, Version = progress.Version };
    }

    private async Task LoadAsync()
    {
        sources = null; selected = null; revisions = null; progress = null; sigmaRules = null; signatureFiles = null; iocIssues = null; signatureEvidence = null; shownRulesRevision = null; error = null;
        try
        {
            sources = await Status.GetSourcesAsync(Math.Clamp(SourcePage ?? 1, 1, 1000), HttpContext.RequestAborted);
            selected = SourceId is { } id ? sources.Items.FirstOrDefault(x => x.Id == id) : sources.Items.FirstOrDefault();
            if (SourceId is not null && selected is null) { error = "找不到此頁的情報來源，請重新選擇。"; return; }
            if (selected is null) return;
            revisions = await Status.GetRevisionsAsync(selected.Id, Math.Clamp(VersionPage ?? 1, 1, 1000), HttpContext.RequestAborted);
            progress = selected.Key == "github-reviewed" ? await Status.GetAdvisoryProgressAsync(selected.Id, HttpContext.RequestAborted) : null;
            if (selected.Key == "sigmahq-rules" && (RulesRevision ?? revisions?.Items.FirstOrDefault()?.Id) is { } revisionId)
            {
                sigmaRules = await Status.GetSigmaRulesAsync(selected.Id, revisionId, Math.Clamp(RulePage ?? 1, 1, 1000), HttpContext.RequestAborted);
                if (sigmaRules is null) error = "找不到所選的規則版本，請重新選擇。";
                else shownRulesRevision = revisionId;
            }
            else if (selected.Key == "signature-base" && (RulesRevision ?? revisions?.Items.FirstOrDefault()?.Id) is { } signatureRevision)
            {
                signatureFiles = await Status.GetSignatureFilesAsync(selected.Id, signatureRevision, Math.Clamp(RulePage ?? 1, 1, 1000), HttpContext.RequestAborted);
                if (signatureFiles is null) error = "找不到所選的來源版本，請重新選擇。";
                else shownRulesRevision = signatureRevision;
                if (signatureFiles is not null && IocPath is { Length: > 0 and <= 512 } path)
                {
                    iocIssues = await Status.GetIocIssuesAsync(selected.Id, signatureRevision, path, Math.Clamp(IssuePage ?? 1, 1, 2000), HttpContext.RequestAborted);
                    if (iocIssues is null) error = "找不到所選的 IOC 來源檔案，請重新選擇。";
                }
                if (signatureFiles is not null && EvidencePath is { Length: > 0 and <= 512 } evidencePath)
                {
                    signatureEvidence = await Status.GetSignatureEvidenceAsync(selected.Id, signatureRevision, evidencePath, HttpContext.RequestAborted,
                        EvidenceLine is { } number ? Math.Clamp(number, 1, 1048576) : null, Math.Clamp(EvidencePage ?? 1, 1, 2000));
                    if (signatureEvidence is null) error = "找不到所選的授權來源檔案，請重新選擇。";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not load intelligence sources: {ErrorType}", ex.GetType().Name);
            error = "無法讀取情報來源，請稍後重新整理。";
        }
    }

    private void InvalidPolicy(EditContext _) { policyOpen = true; message = "設定格式無效，請確認輸入後再試。"; }
    private void InvalidCredential(EditContext _) { policyOpen = true; CredentialInput.Value = null; message = "憑證設定格式無效，請重新輸入。"; }

    private async Task SavePolicyAsync(EditContext _)
    {
        policyOpen = true;
        if (!Actor(out var actor) || PolicyInput is null) return;
        if (!ValidGuid("PolicyInput.SourceId") || !ReadBoolean("PolicyInput.Enabled", out var enabled) ||
            !ValidInteger("PolicyInput.Interval") || !ValidInteger("PolicyInput.Stale") ||
            !ValidMiB("PolicyInput.MiB", out var bytes) || !ValidInteger("PolicyInput.Records"))
        { message = "設定格式無效，請確認輸入後再試。"; return; }
        PolicyInput.Enabled = enabled;
        var input = new IntelligencePolicyUpdate(PolicyInput.Enabled, PolicyInput.Interval, PolicyInput.Stale, bytes, PolicyInput.Records, PolicyInput.Version ?? "");
        if (!IntelligencePolicy.Valid(input)) { message = "設定超出允許範圍，請檢查數值。"; return; }
        try
        {
            var result = await Policy.UpdateAsync(PolicyInput.SourceId, input, actor, HttpContext.RequestAborted);
            message = result switch { IntelligencePolicyResult.Updated or IntelligencePolicyResult.Unchanged => "設定已儲存。", IntelligencePolicyResult.Invalid => "容量或筆數超出此來源允許的上限。", _ => "來源已變更或不存在，請重新整理。" };
            await LoadAsync();
            if (result is IntelligencePolicyResult.Updated or IntelligencePolicyResult.Unchanged) { ResetForms(); policyOpen = false; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private async Task ActivateAsync(EditContext _)
    {
        if (!ValidGuid("ActivationInput.SourceId") || !ValidGuid("ActivationInput.RevisionId") ||
            !ValidGuid("ActivationInput.ExpectedActiveId", optional: true) || !ReadBoolean("ActivationInput.ConfirmReduction", out var confirmed) ||
            !ReadBoolean("ActivationInput.ConfirmOffline", out var offlineConfirmed))
        { message = "版本資料格式無效，請重新選擇。"; return; }
        ActivationInput.ConfirmReduction = confirmed;
        if (!Actor(out var actor) || ActivationInput?.RevisionId is not { } revision || revision == Guid.Empty) { message = "請選擇要啟用的版本。"; return; }
        try
        {
            var result = await Publication.ActivateAsync(ActivationInput.SourceId, revision, ActivationInput.ExpectedActiveId,
                ActivationInput.ExpectedVersion ?? "", ActivationInput.ConfirmReduction, offlineConfirmed, actor, HttpContext.RequestAborted);
            message = result switch { IntelligencePublicationResult.Published or IntelligencePublicationResult.Unchanged => selected?.Key is "sigmahq-rules" or "signature-base" ? "已選用來源版本。" : "版本已啟用。", IntelligencePublicationResult.ConfirmationRequired => "此版本的紀錄數大幅減少；請確認後再選用。", IntelligencePublicationResult.OfflineConfirmationRequired => "請確認離線檔案來源及使用條件後再啟用。", IntelligencePublicationResult.Unavailable => "版本或來源目前無法使用，請檢查授權、資料狀態與撤回紀錄。", _ => "版本已變更或不存在，請重新整理。" };
            await LoadAsync();
            if (result is IntelligencePublicationResult.Published or IntelligencePublicationResult.Unchanged) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private async Task SaveCredentialAsync(EditContext _)
    {
        policyOpen = true;
        if (!Actor(out var actor) || CredentialInput is null) return;
        if (!ValidGuid("CredentialInput.SourceId") || !ReadBoolean("CredentialInput.Clear", out var clear))
        { CredentialInput.Value = null; message = "憑證設定格式無效，請重新輸入。"; return; }
        CredentialInput.Clear = clear;
        var input = new AdvisoryCredentialUpdate(CredentialInput.Version ?? "", CredentialInput.Clear, CredentialInput.Clear ? null : CredentialInput.Value);
        CredentialInput.Value = null;
        if (!AdvisoryCredentials.Valid(input)) { message = "請輸入有效憑證，或勾選清除憑證。"; return; }
        try
        {
            var result = await Credentials.UpdateAsync(CredentialInput.SourceId, input, actor, HttpContext.RequestAborted);
            message = result is AdvisoryCredentialResult.Updated or AdvisoryCredentialResult.Unchanged ? "憑證設定已儲存。" : "來源已變更或不存在，請重新整理。";
            await LoadAsync();
            if (result is AdvisoryCredentialResult.Updated or AdvisoryCredentialResult.Unchanged) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private async Task RevokeAsync(EditContext _)
    {
        if (!ValidGuid("RevocationInput.SourceId") || !ValidGuid("RevocationInput.RevisionId") ||
            !ReadBoolean("RevocationInput.Confirm", out var confirmed) || !confirmed ||
            !Actor(out var actor) || RevocationInput.RevisionId is not { } revision || revision.Version != 7)
        { message = "請選擇版本並確認撤銷。"; return; }
        try
        {
            var result = await Revocation.RevokeAsync(RevocationInput.SourceId, revision, actor, HttpContext.RequestAborted);
            message = result is IntelligenceRevocationResult.Revoked or IntelligenceRevocationResult.AlreadyRevoked ? "版本已撤銷。" : "找不到所選版本。";
            await LoadAsync();
            if (result is IntelligenceRevocationResult.Revoked or IntelligenceRevocationResult.AlreadyRevoked) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private async Task RecoverAsync(EditContext _)
    {
        if (!Actor(out var actor) || RecoveryInput is null) return;
        if (!ValidGuid("RecoveryInput.SourceId") || !ValidGuid("RecoveryInput.ImportId"))
        { message = "匯入資料格式無效，請重新整理。"; return; }
        try
        {
            var result = await Recovery.AbandonAsync(RecoveryInput.SourceId, new(RecoveryInput.ImportId, RecoveryInput.Version ?? ""), actor, HttpContext.RequestAborted);
            message = result == AdvisoryRecoveryResult.Abandoned ? "已放棄失敗匯入，背景工作將依排程重試。" : "匯入狀態已變更，請重新整理。";
            await LoadAsync();
            if (result == AdvisoryRecoveryResult.Abandoned) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private void ResetForms()
    {
        if (selected is null) return;
        PolicyInput = new() { SourceId = selected.Id, Version = selected.Version, Enabled = selected.Enabled,
            Interval = selected.UpdateIntervalSeconds, Stale = selected.StaleAfterSeconds, MiB = selected.MaximumBytes / 1048576m, Records = selected.MaximumRecords };
        ActivationInput = new() { SourceId = selected.Id, ExpectedActiveId = selected.ActiveRevisionId, ExpectedVersion = selected.ActivationVersion };
        CredentialInput = new() { SourceId = selected.Id, Version = selected.Version };
        RevocationInput = new() { SourceId = selected.Id };
        RecoveryInput = progress?.ImportId is { } id ? new() { SourceId = selected.Id, ImportId = id, Version = progress.Version } : new();
    }

    private bool Actor(out Guid actor) => Guid.TryParse(HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out actor) && actor != Guid.Empty;
    private bool ValidGuid(string name, bool optional = false) => HttpContext.Request.Form.TryGetValue(name, out var value) && value.Count == 1 &&
        (optional && string.IsNullOrEmpty(value[0]) || Guid.TryParse(value[0], out var id) && id != Guid.Empty);
    private bool ValidInteger(string name) => HttpContext.Request.Form.TryGetValue(name, out var value) && value.Count == 1 &&
        int.TryParse(value[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    private bool ValidMiB(string name, out int bytes)
    {
        bytes = 0;
        if (!HttpContext.Request.Form.TryGetValue(name, out var value) || value.Count != 1 ||
            !decimal.TryParse(value[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var mib) || mib < 0 || mib > 1024) return false;
        var exact = mib * 1048576m;
        if (exact != decimal.Truncate(exact)) return false;
        bytes = (int)exact;
        return true;
    }
    private bool ReadBoolean(string name, out bool value)
    {
        value = false;
        if (!HttpContext.Request.Form.TryGetValue(name, out var values)) return true;
        return values.Count == 1 && bool.TryParse(values[0], out value);
    }
    private void LogFailure(Exception ex) { Logger.LogWarning("Intelligence operation failed: {ErrorType}", ex.GetType().Name); message = "無法確認操作結果，請重新整理後檢查。"; }
    private string SourceUrl(Guid id) => $"/settings/intelligence?source={id:D}&sourcePage={sources?.Page ?? 1}";
    private string VersionUrl(int page) => $"/settings/intelligence?source={selected?.Id:D}&sourcePage={sources?.Page ?? 1}&versionPage={page}";
    private string RulesUrl(Guid revision, int page) => $"/settings/intelligence?source={selected?.Id:D}&sourcePage={sources?.Page ?? 1}&versionPage={revisions?.Page ?? 1}&rulesRevision={revision:D}&rulePage={page}";
    private string IocIssuesUrl(Guid revision, string path, int page) => $"{RulesUrl(revision, signatureFiles?.Page ?? 1)}&iocPath={Uri.EscapeDataString(path)}&issuePage={page}";
    private string EvidenceUrl(Guid revision, string path, int? line = null, int? page = null) => $"{RulesUrl(revision, signatureFiles?.Page ?? 1)}&evidencePath={Uri.EscapeDataString(path)}&evidencePage={page ?? signatureEvidence?.Page ?? 1}{(line is null ? "" : $"&evidenceLine={line}")}";
    private static string IocIssueReason(string reason) => reason switch
    {
        "invalid-hash" => "雜湊格式無效", "invalid-c2" => "網域或 IP 格式無效", "noncanonical-ip" => "IP 格式不明確",
        "filename-regex-not-supported" => "尚不支援檔名規則", "unrecognized-ioc-format" => "尚不支援此來源格式",
        "line-too-long" => "內容超過長度限制", _ => "原因未明"
    };
    private static string DisplayTime(DateTimeOffset? time) => time?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚無資料";
    private static string Coverage(IntelligenceSourceView source) => source.Key switch { "cisa-kev" => "CISA 已知遭利用的漏洞；不包含所有漏洞，也無法單獨判定設備是否受影響。", "github-reviewed" => "GitHub 已審核的套件安全公告；不包含未審核或惡意軟體公告，不能單獨判定設備是否受影響。", "debian-security" => "Debian 各發行版本的來源套件漏洞狀態；實際設備是否受影響需結合盤點資料判斷。", "ubuntu-osv" => "Canonical Ubuntu 漏洞資料；設備是否受影響需結合發行版本與套件盤點判斷。", "sigmahq-rules" => "保存 SigmaHQ 行為規則；支援的規則可試跑後啟用，不代表所有規則或設備均已涵蓋。", "signature-base" => "保存 IOC 與 YARA 原始檔案；IOC 來源可依條件選用，YARA 規則授權與相容性仍待核對。", _ => source.Description?.Coverage ?? "無法確認來源涵蓋範圍。" };
    private static string Category(IntelligenceSourceView source) => source.Key switch { "cisa-kev" => "已知遭利用的漏洞", "github-reviewed" => "開源套件安全公告", "debian-security" or "ubuntu-osv" => "發行版安全公告", "sigmahq-rules" => "行為規則", "signature-base" => "IOC／YARA", "compliance-as-code-shadow" or "powerstig-network-selection" => "安全組態檢查", _ => source.Description?.Category ?? "未知" };
    private static string PlatformCoverage(IntelligenceSourceView source) => source.Key switch
    {
        "cisa-kev" => "涵蓋多種平台與產品；是否受影響需依產品及版本另行判斷。",
        "github-reviewed" => "各公告所列的開源套件生態系；是否影響設備尚未在此判斷。",
        "debian-security" => "所選情報版本涵蓋的 Debian 發行版本。",
        "ubuntu-osv" => "所選情報版本涵蓋的 Ubuntu 發行版本。",
        "sigmahq-rules" => "依各規則的平台與事件來源而異；實際可用性取決於規則支援與設備已收集的資料。",
        "signature-base" => "依各檔案與規則的平台和相依條件而異；消費端相容性尚未驗證。",
        _ => source.Description?.PlatformCoverage ?? "未知"
    };
    private string? ImportMessage => ImportResult switch
    {
        "saved" => "匯入完成，請確認來源及版本資訊後再選擇使用。",
        "invalid" => "檔案格式不正確、超出限制或內容已被撤銷。",
        "unavailable" => "來源已停用或不存在，無法匯入。",
        "retry" => "來源正在更新或設定已變更，請稍後再試。",
        "error" => "匯入失敗，請稍後再試。", _ => null
    };
    private static string ErrorText(string error) => error.StartsWith("Automatic intelligence activation failed.", StringComparison.Ordinal) ? "自動啟用尚未完成；若下載也失敗，系統會一併重試。" : error switch { "KEV retrieval failed." or "Sigma retrieval failed." or "Signature-Base retrieval failed." => "無法取得來源資料。", "KEV retrieval timed out." or "Sigma retrieval timed out." or "Signature-Base retrieval timed out." => "取得來源資料逾時。", "KEV content or response failed validation." or "Sigma source failed validation." or "Signature-Base source failed validation." => "來源資料未通過驗證。", "Sigma license unavailable or changed." or "Signature-Base license unavailable or changed." => "來源授權檔遺失或已變更，必須重新確認授權。", "The retrieved KEV revision is revoked or incomplete." or "Sigma revision is revoked or incomplete." or "Signature-Base revision is revoked or incomplete." => "取得的版本已撤銷或不完整。", _ => "更新未完成，請檢查來源狀態。" };
    private static string FailureText(string code) => code switch { "fetch-failed" => "無法取得來源資料。", "request-timeout" => "取得來源資料逾時。", "invalid-response" => "來源資料不一致或未通過驗證。", "cursor-rejected" => "來源分頁失效。", "limit-exceeded" => "資料超過設定的容量或筆數上限。", "invalid-base" => "目前基礎版本不可使用。", "credential-unavailable" => "無法讀取來源憑證，請重新設定。", _ => "無法完成匯入。" };
    private static string SourceState(IntelligenceSourceView source) => !source.Enabled ? "已停用" : source.ActiveUnavailable ? "目前版本不可使用" : source.ActiveVersionOutdated ? "選用的版本不是最新確認資料" : source.IsStale ? "資料已過期或尚未更新" : source.LastError is not null ? "更新失敗" : source.Key is "signature-base" or "sigmahq-rules" && source.ActiveRevisionId is null ? "已匯入供檢視" : source.ActiveRevisionId is null ? "尚未啟用版本" : "已選用來源版本";
    private static string ProgressState(string state) => state switch { "disabled" => "已停用", "not-started" => "尚未開始", "importing" => "匯入中", "reconciling" => "驗證中", "inconsistent" => "資料不一致", "failed" => "失敗", "rate-limited" => "等待來源額度", "awaiting-activation" => "等待啟用", "active" => "已啟用", "active-unavailable" or "candidate-unavailable" => "版本不可使用", "candidate-stale" => "候選版本已過期", _ => "已停止" };

    private sealed class PolicyForm { public Guid SourceId { get; set; } public string? Version { get; set; } public bool Enabled { get; set; } public int Interval { get; set; } public int Stale { get; set; } public decimal MiB { get; set; } public int Records { get; set; } }
    private sealed class ActivationForm { public Guid SourceId { get; set; } public Guid? RevisionId { get; set; } public Guid? ExpectedActiveId { get; set; } public string? ExpectedVersion { get; set; } public bool ConfirmReduction { get; set; } public bool ConfirmOffline { get; set; } }
    private sealed class CredentialForm { public Guid SourceId { get; set; } public string? Version { get; set; } public bool Clear { get; set; } public string? Value { get; set; } }
    private sealed class RecoveryForm { public Guid SourceId { get; set; } public Guid ImportId { get; set; } public string? Version { get; set; } }
    private sealed class RevocationForm { public Guid SourceId { get; set; } public Guid? RevisionId { get; set; } public bool Confirm { get; set; } }
}
