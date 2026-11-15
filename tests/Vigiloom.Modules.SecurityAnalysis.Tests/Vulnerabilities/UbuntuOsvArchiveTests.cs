using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuOsvArchiveTests
{
    // Synthetic TAR/XZ fixtures; their single small OSV document is not upstream data.
    internal const string Valid = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/AKZdADecyyKFMzcEG3RPRTXKZYR17KFG4vdcAclEy+MNtLsWtizntgQr70BcDeXrxefElT/dMlvyZrCRJBrduNkk7YGnSC0mE04aDfK/bvCMqYFWZ4WyPDnRBgIdi76wcpQ27RrasmqfLs9zoYM0LzEQGvvDB+fWQlAaACnSe9BvFiXPLkPVP70PZK0T9DhjH7Sjaz61JnlDoK+hsMFoz91ys7OEa4kHNwAAAACmjXWCZuRa0QABwgGAUAAAWdrwI7HEZ/sCAAAAAARZWg==";
    private const string Duplicate = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/AKpdADecyyKFMzcEG3RPRTXKZYR17KFG4vdcAclEy+MNtLsWtizntgQr70BcDeXrxefElT/dMlvyZrCRJBrduNkk7YGnSC0mE04aDfK/bvCMqYFWZ4WyPDnRBgIdi76wcpQ27RrasmqfLs9zoYM0LzEQGvvDB+fWQlAaACnSe9BvEyTQ0fkoVzrs9lUYQpR1rFaOAwSbkL5PGTbGxmHEq6qiHOpXtOuHUO6LW+oAAAAAzSJggn5t140AAcYBgFAAAE+YYbixxGf7AgAAAAAEWVo=";
    private const string Traversal = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/AJddABdguMRhMUUNH2fb49Q6yg+bsVnEh00B4KdaPHcBIBBVr1J1PsP2TJQvXMSHv/jQYkAWPdG8nlkvGU1oiL3Idz9NQeW2h8K/6uUlcDztz/Abp3ovfbcLWXCMjhjNNO1UiXg0ToTiXo9MM8ckJ9zxR5NDKJyTgln90ZXmYDWgEvyD4U9c+B/XMB7ZNKFl08f3XrqcSht+xgAAAL8NYrfuTfJYAAGzAYBQAAA9BI7jscRn+wIAAAAABFla";
    private const string OversizedPax = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4T//APxdABcL3ASAUDIm7h19G/PyyzPs7Pg39yGppR65Ok9e++f5EVyupgPGuIg5u8seSddDoR7SUFHELKtboEoIlKu23ohmQzVYtnqIkVOE6x4kGMALqSGCL2hYl1+JQ/UnQj2Vg9j5RZB9fGCcWMDWRebuJvv3EgYcuCPR+yWA2XPykpAd3ji+89exysUOF03ERlE9d/aXKoEIzeZ6at4l335UFhVxkE5Ol6pJfG7m4dO/SEcHC8HSkmjw0CJxYcbBNk5isX+NbGVhDzxoDKng76sQR3EdZYAwzpVzSCDk7CqwrfIB/WWFNODdLj1/ZlBWha7wswWZvPWC8eCWlPKeAADDYoPrMzg0VAABmAKAgAUAbUq7ObHEZ/sCAAAAAARZWg==";

    [TestMethod]
    public async Task Read_StreamsRecordsAndValidatesCompleteArchive()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(Valid));
        var records = new List<UbuntuOsvRecord>();
        byte[]? document = null;
        var count = await UbuntuOsvArchive.ReadAsync(stream, 20_000, 10, 1024,
            (record, bytes, _) => { records.Add(record); document = bytes.ToArray(); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual(1, count);
        Assert.AreEqual("UBUNTU-CVE-2025-0012", records.Single().Id);
        Assert.IsNotNull(document);
        Assert.AreEqual(records.Single().Id, UbuntuOsvReader.Read(document, 1024).Id);
    }

    [TestMethod]
    public async Task Read_RejectsDuplicateTraversalLimitsAndTruncation()
    {
        static Task<int> Read(byte[] bytes, long expanded = 20_000, int document = 1024) =>
            UbuntuOsvArchive.ReadAsync(new MemoryStream(bytes), expanded, 10, document, (_, _, _) => ValueTask.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Convert.FromBase64String(Duplicate)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Convert.FromBase64String(Traversal)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Convert.FromBase64String(Valid), expanded: 500));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Convert.FromBase64String(Valid), document: 10));
        var full = Convert.FromBase64String(Valid);
        await Assert.ThrowsAsync<Exception>(() => Read(full[..^8]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(Convert.FromBase64String(OversizedPax), expanded: 100_000));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([.. full, 1, 2, 3]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([.. full, .. full]));
    }
}
