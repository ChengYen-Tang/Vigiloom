# NDT7 reference adapter

The `reference` directory contains the library sources from m-lab/ndt7-client-go v0.10.1, commit 1f6adcf81a3f29cae933a2e317e86e24826e2900 (Apache-2.0). Its license/authors are retained. CLI, test and exporter sources are excluded. One documented adaptation exposes the completion error otherwise discarded by `collectData`; the measurement algorithms are unchanged.

The Vigiloom wrapper enforces a shared transport-byte budget, TLS validation, public resolved destinations, cancellation/deadline, one selected Locate target and no automatic retry. Inputs are local stdin JSON, output is a bounded JSON summary; no access token or arbitrary error/URL is logged. It uses the reference library's download/upload measurements and server TCP MinRTT; unknown fields remain absent.

Go 1.27.3 is a build dependency only; published Worker output includes the appropriate executable. The user installs no Go toolchain. Tests use private local fixtures and do not contact M-Lab. Real external tests require explicit informed consent and consume the same persistent budget/rate limits as any user test.
