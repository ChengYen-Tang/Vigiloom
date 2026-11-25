# Identity service data protection

When splitting Identity from Web, configure both hosts to use the existing
`Vigiloom:DataProtection:ApplicationName`, `KeyRingPath`, and, when configured,
the same `CertificatePath` and `CertificatePassword`. The key ring must be
accessible to every relevant instance. Preserve the existing keys: account
authenticator keys and recovery codes are encrypted with them. A new key ring
cannot decrypt previously stored two-factor secrets.

The Identity service and its `/internal/identity` endpoints are for the
restricted internal network and must not be exposed by the public gateway.
