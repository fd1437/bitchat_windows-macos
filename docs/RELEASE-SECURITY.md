# Release security and user-data isolation — 0.1.9

## Findings

The inspected 0.1.8 macOS application contains the compiled executable, assets,
localizations and tracked map/relay resources. Its resources match the release
allowlist and the checked static-resource hashes. No profile/history database was
found among the packaged files. The previous Windows publish artifact contains an
unnecessary Core PDB; the installer already selected its EXE explicitly rather
than installing that PDB. Release builds now omit the Core PDB as well.

Both clients load existing user profiles separately from the installed application.
In-place installation intentionally keeps this data. Seeing one's messages after
reinstallation alone does not establish a distribution leak. No user's history
was erased as part of this change.

## Enforced checks

- `scripts/audit_release.py source`: reject tracked runtime profiles, databases,
  debug dumps, signing material and common credentials; verify recorded binary
  and source-archive digests for the bundled Windows dependencies.
- `scripts/audit_release.py windows`: explicit file allowlist, required files,
  rejection of symlinks and matching hashes for packaged helper dependencies.
- `scripts/audit_release.py macos`: explicit bundle/resource allowlist, required
  resources, rejection of symlinks, and hashes for maps, relay data and README.
- Six rejection tests cover accidental profile inclusion, missing binaries,
  symbolic links, dependency substitution and credentials without logging values.
- NuGet transitive vulnerability audit blocks Windows builds on reported advisories
  or vulnerability-feed errors. It does not cover every native/Swift dependency.
- The Windows validation runner runs the interoperability suite and native
  diagnostics. New checks verify empty fresh profiles, independent identities,
  existing-profile preservation and update URLs scoped to this repository/tag.
- Windows publish files must retain the same hashes after UI/native diagnostics.
  Installer packaging requires the exact validated source commit, repository and
  workflow. NSIS inputs are explicit paths; unknown directory contents are not
  recursively swept into the installer.
- macOS release builds compile only a Git archive of committed source, with fresh
  build products on a clean GitHub runner. No application launch, personal profile
  or user keychain is needed. Extended attributes/resource forks are not copied.
  The extracted ZIP is audited again; DMG and SHA-256 checks are verified.

## Evidence and limits

Release assets include `SECURITY-*.json` inventories, SHA-256 checksums, build
information and source archives/manifests. Inventories contain relative paths and
digests only. Native diagnostics use temporary synthetic profiles and do not
publish personal conversation contents.

An allowlist cannot recognize arbitrary conversation text deliberately embedded
inside source code or prove the safety of arbitrary compiled code. Dependency
advisories can be incomplete. This review is not an independent penetration test,
malware certification or complete cryptographic audit. Radio interoperability still
requires physical-device testing. Packages remain without publisher certificates;
SHA-256 from the same GitHub release checks download integrity, not an independent
publisher signature. macOS keeps its certificate-free ad-hoc code seal.
