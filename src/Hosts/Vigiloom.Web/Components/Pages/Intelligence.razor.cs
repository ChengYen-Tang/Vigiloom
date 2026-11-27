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
    private IReadOnlyDictionary<Guid, BaselineCheck> localVersions = new Dictionary<Guid, BaselineCheck>();

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
            IocDetectionEnabled = selected.IocDetectionEnabled,
            Interval = selected.UpdateIntervalSeconds, Stale = selected.StaleAfterSeconds, MiB = selected.MaximumBytes / 1048576m, Records = selected.MaximumRecords };
        if (!submittedActivation) ActivationInput = new() { SourceId = selected.Id, ExpectedActiveId = selected.ActiveRevisionId, ExpectedVersion = selected.ActivationVersion };
        if (!submittedCredential) CredentialInput = new() { SourceId = selected.Id, Version = selected.Version };
        if (!submittedRecovery && progress?.ImportId is { } id) RecoveryInput = new() { SourceId = selected.Id, ImportId = id, Version = progress.Version };
    }

    private async Task LoadAsync()
    {
        sources = null; selected = null; revisions = null; progress = null; sigmaRules = null; signatureFiles = null; iocIssues = null; signatureEvidence = null; shownRulesRevision = null; error = null;
        localVersions = new Dictionary<Guid, BaselineCheck>();
        try
        {
            sources = await Status.GetSourcesAsync(Math.Clamp(SourcePage ?? 1, 1, 1000), HttpContext.RequestAborted);
            selected = SourceId is { } id ? sources.Items.FirstOrDefault(x => x.Id == id) : sources.Items.FirstOrDefault();
            if (SourceId is not null && selected is null) { error = L["找不到此頁的情報來源，請重新選擇。"].Value; return; }
            if (selected is null) return;
            revisions = await Status.GetRevisionsAsync(selected.Id, Math.Clamp(VersionPage ?? 1, 1, 1000), HttpContext.RequestAborted);
            if (selected.Key.StartsWith(BaselineSources.LocalPrefix, StringComparison.Ordinal) && revisions is not null)
                localVersions = await CustomBaselines.ReadVersionsAsync(selected.Id, revisions.Items.Select(x => x.Id).ToArray(), HttpContext.RequestAborted);
            progress = selected.Key == "github-reviewed" ? await Status.GetAdvisoryProgressAsync(selected.Id, HttpContext.RequestAborted) : null;
            if (selected.Key == "sigmahq-rules" && (RulesRevision ?? revisions?.Items.FirstOrDefault()?.Id) is { } revisionId)
            {
                sigmaRules = await Status.GetSigmaRulesAsync(selected.Id, revisionId, Math.Clamp(RulePage ?? 1, 1, 1000), HttpContext.RequestAborted);
                if (sigmaRules is null) error = L["找不到所選的規則版本，請重新選擇。"].Value;
                else shownRulesRevision = revisionId;
            }
            else if (selected.Key == "signature-base" && (RulesRevision ?? revisions?.Items.FirstOrDefault()?.Id) is { } signatureRevision)
            {
                signatureFiles = await Status.GetSignatureFilesAsync(selected.Id, signatureRevision, Math.Clamp(RulePage ?? 1, 1, 1000), HttpContext.RequestAborted);
                if (signatureFiles is null) error = L["找不到所選的來源版本，請重新選擇。"].Value;
                else shownRulesRevision = signatureRevision;
                if (signatureFiles is not null && IocPath is { Length: > 0 and <= 512 } path)
                {
                    iocIssues = await Status.GetIocIssuesAsync(selected.Id, signatureRevision, path, Math.Clamp(IssuePage ?? 1, 1, 2000), HttpContext.RequestAborted);
                    if (iocIssues is null) error = L["找不到所選的 IOC 來源檔案，請重新選擇。"].Value;
                }
                if (signatureFiles is not null && EvidencePath is { Length: > 0 and <= 512 } evidencePath)
                {
                    signatureEvidence = await Status.GetSignatureEvidenceAsync(selected.Id, signatureRevision, evidencePath, HttpContext.RequestAborted,
                        EvidenceLine is { } number ? Math.Clamp(number, 1, 1048576) : null, Math.Clamp(EvidencePage ?? 1, 1, 2000));
                    if (signatureEvidence is null) error = L["找不到所選的授權來源檔案，請重新選擇。"].Value;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not load intelligence sources: {ErrorType}", ex.GetType().Name);
            error = L["無法讀取情報來源，請稍後重新整理。"].Value;
        }
    }

    private void InvalidPolicy(EditContext _) { policyOpen = true; message = L["設定格式無效，請確認輸入後再試。"].Value; }
    private void InvalidCredential(EditContext _) { policyOpen = true; CredentialInput.Value = null; message = L["憑證設定格式無效，請重新輸入。"].Value; }

    private async Task SavePolicyAsync(EditContext _)
    {
        policyOpen = true;
        if (!Actor(out var actor) || PolicyInput is null) return;
        if (!ValidGuid("PolicyInput.SourceId") || !ReadBoolean("PolicyInput.Enabled", out var enabled) ||
            !ValidInteger("PolicyInput.Interval") || !ValidInteger("PolicyInput.Stale") ||
            !ValidMiB("PolicyInput.MiB", out var bytes) || !ValidInteger("PolicyInput.Records"))
        { message = L["設定格式無效，請確認輸入後再試。"].Value; return; }
        PolicyInput.Enabled = enabled;
        bool? iocEnabled = null;
        if (selected?.Key == "signature-base")
        {
            if (!ReadBoolean("PolicyInput.IocDetectionEnabled", out var enabledIoc)) { message = L["設定格式無效，請確認輸入後再試。"].Value; return; }
            iocEnabled = enabledIoc;
        }
        var input = new IntelligencePolicyUpdate(PolicyInput.Enabled, PolicyInput.Interval, PolicyInput.Stale, bytes, PolicyInput.Records, PolicyInput.Version ?? "", iocEnabled);
        if (!IntelligencePolicy.Valid(input)) { message = L["設定超出允許範圍，請檢查數值。"].Value; return; }
        try
        {
            var result = await Policy.UpdateAsync(PolicyInput.SourceId, input, actor, HttpContext.RequestAborted);
            message = result switch { IntelligencePolicyResult.Updated or IntelligencePolicyResult.Unchanged => L["設定已儲存。"].Value, IntelligencePolicyResult.Invalid => L["容量或筆數超出此來源允許的上限。"].Value, _ => L["來源已變更或不存在，請重新整理。"].Value };
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
        { message = L["版本資料格式無效，請重新選擇。"].Value; return; }
        ActivationInput.ConfirmReduction = confirmed;
        if (!Actor(out var actor) || ActivationInput?.RevisionId is not { } revision || revision == Guid.Empty) { message = L["請選擇要啟用的版本。"].Value; return; }
        try
        {
            var result = await Publication.ActivateAsync(ActivationInput.SourceId, revision, ActivationInput.ExpectedActiveId,
                ActivationInput.ExpectedVersion ?? "", ActivationInput.ConfirmReduction, offlineConfirmed, actor, HttpContext.RequestAborted);
            message = result switch { IntelligencePublicationResult.Published or IntelligencePublicationResult.Unchanged => selected?.Key is "sigmahq-rules" or "signature-base" ? L["已選用來源版本。"].Value : L["版本已啟用。"].Value, IntelligencePublicationResult.ConfirmationRequired => L["此版本的紀錄數大幅減少；請確認後再選用。"].Value, IntelligencePublicationResult.OfflineConfirmationRequired => L["請確認離線檔案來源及使用條件後再啟用。"].Value, IntelligencePublicationResult.Unavailable => L["版本或來源目前無法使用，請檢查授權、資料狀態與撤回紀錄。"].Value, _ => L["版本已變更或不存在，請重新整理。"].Value };
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
        { CredentialInput.Value = null; message = L["憑證設定格式無效，請重新輸入。"].Value; return; }
        CredentialInput.Clear = clear;
        var input = new AdvisoryCredentialUpdate(CredentialInput.Version ?? "", CredentialInput.Clear, CredentialInput.Clear ? null : CredentialInput.Value);
        CredentialInput.Value = null;
        if (!AdvisoryCredentials.Valid(input)) { message = L["請輸入有效憑證，或勾選清除憑證。"].Value; return; }
        try
        {
            var result = await Credentials.UpdateAsync(CredentialInput.SourceId, input, actor, HttpContext.RequestAborted);
            message = result is AdvisoryCredentialResult.Updated or AdvisoryCredentialResult.Unchanged ? L["憑證設定已儲存。"].Value : L["來源已變更或不存在，請重新整理。"].Value;
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
        { message = L["請選擇版本並確認撤銷。"].Value; return; }
        try
        {
            var result = await Revocation.RevokeAsync(RevocationInput.SourceId, revision, actor, HttpContext.RequestAborted);
            message = result is IntelligenceRevocationResult.Revoked or IntelligenceRevocationResult.AlreadyRevoked ? L["版本已撤銷。"].Value : L["找不到所選版本。"].Value;
            await LoadAsync();
            if (result is IntelligenceRevocationResult.Revoked or IntelligenceRevocationResult.AlreadyRevoked) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private async Task RecoverAsync(EditContext _)
    {
        if (!Actor(out var actor) || RecoveryInput is null) return;
        if (!ValidGuid("RecoveryInput.SourceId") || !ValidGuid("RecoveryInput.ImportId"))
        { message = L["匯入資料格式無效，請重新整理。"].Value; return; }
        try
        {
            var result = await Recovery.AbandonAsync(RecoveryInput.SourceId, new(RecoveryInput.ImportId, RecoveryInput.Version ?? ""), actor, HttpContext.RequestAborted);
            message = result == AdvisoryRecoveryResult.Abandoned ? L["已放棄失敗匯入，背景工作將依排程重試。"].Value : L["匯入狀態已變更，請重新整理。"].Value;
            await LoadAsync();
            if (result == AdvisoryRecoveryResult.Abandoned) ResetForms();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogFailure(ex); }
    }

    private void ResetForms()
    {
        if (selected is null) return;
        PolicyInput = new() { SourceId = selected.Id, Version = selected.Version, Enabled = selected.Enabled,
            IocDetectionEnabled = selected.IocDetectionEnabled,
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
    private void LogFailure(Exception ex) { Logger.LogWarning("Intelligence operation failed: {ErrorType}", ex.GetType().Name); message = L["無法確認操作結果，請重新整理後檢查。"].Value; }
    private string SourceUrl(Guid id) => $"/settings/intelligence?source={id:D}&sourcePage={sources?.Page ?? 1}";
    private string VersionUrl(int page) => $"/settings/intelligence?source={selected?.Id:D}&sourcePage={sources?.Page ?? 1}&versionPage={page}";
    private string RulesUrl(Guid revision, int page) => $"/settings/intelligence?source={selected?.Id:D}&sourcePage={sources?.Page ?? 1}&versionPage={revisions?.Page ?? 1}&rulesRevision={revision:D}&rulePage={page}";
    private string IocIssuesUrl(Guid revision, string path, int page) => $"{RulesUrl(revision, signatureFiles?.Page ?? 1)}&iocPath={Uri.EscapeDataString(path)}&issuePage={page}";
    private string EvidenceUrl(Guid revision, string path, int? line = null, int? page = null) => $"{RulesUrl(revision, signatureFiles?.Page ?? 1)}&evidencePath={Uri.EscapeDataString(path)}&evidencePage={page ?? signatureEvidence?.Page ?? 1}{(line is null ? "" : $"&evidenceLine={line}")}";
    private string IocIssueReason(string reason) => reason switch
    {
        "invalid-hash" => L["雜湊格式無效"].Value, "invalid-c2" => L["網域或 IP 格式無效"].Value, "noncanonical-ip" => L["IP 格式不明確"].Value,
        "filename-regex-not-supported" => L["尚不支援檔名規則"].Value, "unrecognized-ioc-format" => L["尚不支援此來源格式"].Value,
        "line-too-long" => L["內容超過長度限制"].Value, _ => L["原因未明"].Value
    };
    private string DisplayTime(DateTimeOffset? time) => time?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") ?? L["尚無資料"].Value;
    private string Coverage(IntelligenceSourceView source) => source.Key switch { "cisa-kev" => L["CISA 已知遭利用的漏洞；不包含所有漏洞，也無法單獨判定設備是否受影響。"].Value, "github-reviewed" => L["GitHub 已審核的套件安全公告；不包含未審核或惡意軟體公告，不能單獨判定設備是否受影響。"].Value, "debian-security" => L["Debian 各發行版本的來源套件漏洞狀態；實際設備是否受影響需結合盤點資料判斷。"].Value, "ubuntu-osv" => L["Canonical Ubuntu 漏洞資料；設備是否受影響需結合發行版本與套件盤點判斷。"].Value, "sigmahq-rules" => L["保存 SigmaHQ 行為規則；支援的規則可試跑後啟用，不代表所有規則或設備均已涵蓋。"].Value, "signature-base" => L["保存 IOC 與 YARA 原始檔案；IOC 來源可依條件選用，YARA 規則授權與相容性仍待核對。"].Value, _ => source.Description?.Coverage ?? L["無法確認來源涵蓋範圍。"].Value };
    private string Category(IntelligenceSourceView source) => source.Key switch { "cisa-kev" => L["已知遭利用的漏洞"].Value, "github-reviewed" => L["開源套件安全公告"].Value, "debian-security" or "ubuntu-osv" => L["發行版安全公告"].Value, "sigmahq-rules" => L["行為規則"].Value, "signature-base" => "IOC／YARA", "compliance-as-code-shadow" or "powerstig-network-selection" => L["安全組態檢查"].Value, _ when source.Key.StartsWith(BaselineSources.LocalPrefix, StringComparison.Ordinal) => L["安全組態檢查"].Value, _ => source.Description?.Category ?? L["未知"].Value };
    private string PlatformCoverage(IntelligenceSourceView source) => source.Key switch
    {
        "cisa-kev" => L["涵蓋多種平台與產品；是否受影響需依產品及版本另行判斷。"].Value,
        "github-reviewed" => L["各公告所列的開源套件生態系；是否影響設備尚未在此判斷。"].Value,
        "debian-security" => L["所選情報版本涵蓋的 Debian 發行版本。"].Value,
        "ubuntu-osv" => L["所選情報版本涵蓋的 Ubuntu 發行版本。"].Value,
        "sigmahq-rules" => L["依各規則的平台與事件來源而異；實際可用性取決於規則支援與設備已收集的資料。"].Value,
        "signature-base" => L["依各檔案與規則的平台和相依條件而異；消費端相容性尚未驗證。"].Value,
        _ => source.Description?.PlatformCoverage ?? L["未知"].Value
    };
    private string? ImportMessage => ImportResult switch
    {
        "saved" => L["匯入完成，請確認來源及版本資訊後再選擇使用。"].Value,
        "invalid" => L["檔案格式不正確、超出限制或內容已被撤銷。"].Value,
        "unavailable" => L["來源已停用或不存在，無法匯入。"].Value,
        "retry" => L["來源正在更新或設定已變更，請稍後再試。"].Value,
        "error" => L["匯入失敗，請稍後再試。"].Value, _ => null
    };
    private string ErrorText(string error) => error.StartsWith("Automatic intelligence activation failed.", StringComparison.Ordinal) ? L["自動啟用尚未完成；若下載也失敗，系統會一併重試。"].Value : error switch { "KEV retrieval failed." or "Sigma retrieval failed." or "Signature-Base retrieval failed." => L["無法取得來源資料。"].Value, "KEV retrieval timed out." or "Sigma retrieval timed out." or "Signature-Base retrieval timed out." => L["取得來源資料逾時。"].Value, "KEV content or response failed validation." or "Sigma source failed validation." or "Signature-Base source failed validation." => L["來源資料未通過驗證。"].Value, "Sigma license unavailable or changed." or "Signature-Base license unavailable or changed." => L["來源授權檔遺失或已變更，必須重新確認授權。"].Value, "The retrieved KEV revision is revoked or incomplete." or "Sigma revision is revoked or incomplete." or "Signature-Base revision is revoked or incomplete." => L["取得的版本已撤銷或不完整。"].Value, _ => L["更新未完成，請檢查來源狀態。"].Value };
    private string FailureText(string code) => code switch { "fetch-failed" => L["無法取得來源資料。"].Value, "request-timeout" => L["取得來源資料逾時。"].Value, "invalid-response" => L["來源資料不一致或未通過驗證。"].Value, "cursor-rejected" => L["來源分頁失效。"].Value, "limit-exceeded" => L["資料超過設定的容量或筆數上限。"].Value, "invalid-base" => L["目前基礎版本不可使用。"].Value, "credential-unavailable" => L["無法讀取來源憑證，請重新設定。"].Value, _ => L["無法完成匯入。"].Value };
    private string SourceState(IntelligenceSourceView source) => !source.Enabled ? L["已停用"].Value : source.ActiveUnavailable ? L["目前版本不可使用"].Value : source.ActiveVersionOutdated ? L["選用的版本不是最新確認資料"].Value : source.IsStale ? L["資料已過期或尚未更新"].Value : source.LastError is not null ? L["更新失敗"].Value : source.Key is "signature-base" or "sigmahq-rules" && source.ActiveRevisionId is null ? L["已匯入供檢視"].Value : source.ActiveRevisionId is null ? L["尚未啟用版本"].Value : L["已選用來源版本"].Value;
    private string ProgressState(string state) => state switch { "disabled" => L["已停用"].Value, "not-started" => L["尚未開始"].Value, "importing" => L["匯入中"].Value, "reconciling" => L["驗證中"].Value, "inconsistent" => L["資料不一致"].Value, "failed" => L["失敗"].Value, "rate-limited" => L["等待來源額度"].Value, "awaiting-activation" => L["等待啟用"].Value, "active" => L["已啟用"].Value, "active-unavailable" or "candidate-unavailable" => L["版本不可使用"].Value, "candidate-stale" => L["候選版本已過期"].Value, _ => L["已停止"].Value };

    private sealed class PolicyForm { public Guid SourceId { get; set; } public string? Version { get; set; } public bool Enabled { get; set; } public bool IocDetectionEnabled { get; set; } public int Interval { get; set; } public int Stale { get; set; } public decimal MiB { get; set; } public int Records { get; set; } }
    private sealed class ActivationForm { public Guid SourceId { get; set; } public Guid? RevisionId { get; set; } public Guid? ExpectedActiveId { get; set; } public string? ExpectedVersion { get; set; } public bool ConfirmReduction { get; set; } public bool ConfirmOffline { get; set; } }
    private sealed class CredentialForm { public Guid SourceId { get; set; } public string? Version { get; set; } public bool Clear { get; set; } public string? Value { get; set; } }
    private sealed class RecoveryForm { public Guid SourceId { get; set; } public Guid ImportId { get; set; } public string? Version { get; set; } }
    private sealed class RevocationForm { public Guid SourceId { get; set; } public Guid? RevisionId { get; set; } public bool Confirm { get; set; } }
}
