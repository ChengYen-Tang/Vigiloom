using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Hashes the complete, deterministically ordered assessment payload for retry conflict detection.</summary>
internal static class LinuxAssessmentDigest
{
    internal static byte[] Compute(LinuxPackageAssessment assessment)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashStream(hash);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            json.WriteStringValue(assessment.SourceId!.Value);
            json.WriteStringValue(assessment.RevisionId!.Value);
            json.WriteStringValue(assessment.HostSnapshotId!.Value);
            json.WriteStringValue(assessment.PackageSnapshotId!.Value);
            json.WriteStringValue(assessment.Ecosystem);
            foreach (var finding in assessment.Findings!.OrderBy(x => x.PackageKey, StringComparer.Ordinal).ThenBy(x => x.AdvisoryId, StringComparer.Ordinal))
            {
                json.WriteStartArray();
                json.WriteStringValue(finding.PackageKey); json.WriteStringValue(finding.AdvisoryId);
                json.WriteNumberValue((int)finding.State); json.WriteNumberValue((int)finding.Origin); json.WriteNumberValue((int)finding.Reason);
                json.WriteStringValue(finding.SourcePackage); json.WriteStringValue(finding.SourceVersion);
                json.WriteStringValue(finding.BinaryVersion); json.WriteStringValue(finding.Architecture);
                if (finding.FixStatus is { } fix) json.WriteNumberValue((int)fix); else json.WriteNullValue();
                json.WriteStringValue(finding.FixedVersion);
                json.WriteStringValue(finding.UbuntuPriority);
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
        return hash.GetHashAndReset();
    }

    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
