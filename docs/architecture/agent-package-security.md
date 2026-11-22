# Agent releases and offline packages

Agent releases use GitHub tags `agent-v<version>`. The release workflow publishes self-contained Windows x64, Windows x86 and Linux x64 installer bundles and update archives. A prerelease version is marked Preview. Set `packaging/agent/release.json` before releasing; the workflow also supports a version input. Each version must use a new tag.

An Administrator can list GitHub releases and download selected Agent versions to the central package library. The platform obtains the asset size and SHA-256 digest from the GitHub Release API and verifies downloaded bytes. This checks integrity against GitHub metadata; it is not an independent publisher signature. The platform can operate offline once the desired versions have been downloaded. Unused cached versions can be deleted.

An Agent obtains only the package currently assigned to it by an Administrator. The Updater verifies the assigned manifest and archive hashes before installation. Administrators select a cached earlier version to roll back; no expiration date is required. The Updater is updated alongside an Agent upgrade, but is not downgraded when rolling the Agent back.
