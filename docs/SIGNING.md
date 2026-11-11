# Signing releases (for maintainers)

Unsigned apps trigger "unknown developer" warnings: **SmartScreen** on Windows and **Gatekeeper** on macOS. These warnings are about identity and reputation, not malware detections, but they put people off. This page explains what is already done to keep BSSIDLock from looking suspicious, and how to turn on real code signing.

The [release workflow](../.github/workflows/release.yml) signs automatically once the secrets below exist. Without them it still publishes working, unsigned builds.

## Already in place

These help with antivirus heuristics and reputation, and cost nothing:

- **Built in public CI** from a tagged commit, never on a personal machine.
- **SHA-256 checksums** and **GitHub build-provenance attestations** on every release (`gh attestation verify`).
- **Windows exe hygiene:**
  - Plain .NET Framework IL, with no packer, single-file bundle, self-extractor or obfuscator. These are the main causes of antivirus false positives.
  - Complete version resource: company, product, description, copyright.
  - `asInvoker` manifest, so it never asks for administrator rights.
  - Deterministic build, small size (about 350 KB), and no network code.
- **macOS:** hardened runtime and an entitlements file are ready for Developer ID signing. Ad-hoc signature otherwise.

## Windows code signing

Since June 2023, publicly trusted code-signing keys must live on hardware (HSM/token), so a `.pfx` file in a CI secret is no longer an option for new certificates. Practical choices:

| Option | Cost | Notes |
| --- | --- | --- |
| **[SignPath Foundation](https://signpath.org)** | Free for OSS | Signs open-source projects after review. The workflow already has the step. |
| [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) | ~US$10/month | Microsoft's own service; use `azure/trusted-signing-action`. Identity validation required. |
| OV certificate (Certum, Sectigo, …) | ~US$70–400/year | Cloud HSM or USB token; signing in CI needs the vendor's tooling. |

### SignPath setup (recommended)

1. Apply at <https://signpath.org/apply> with this repository.
2. In SignPath, create a project (slug `bssid-lock`) with a signing policy (slug `release-signing`) and link it to GitHub as a trusted build system.
3. In GitHub, go to **Settings → Secrets and variables → Actions**:
   - Secret `SIGNPATH_API_TOKEN`
   - Variable `SIGNPATH_ORGANIZATION_ID`
   - Optional variables `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_POLICY_SLUG`, if you used different slugs
4. Push a tag (`git tag v1.2.0 && git push origin v1.2.0`). The workflow uploads the unsigned exe, waits for SignPath to sign it, and publishes the signed file.

**SmartScreen reputation:** even signed files can show a warning until enough people have downloaded them. Reputation follows the certificate, so it builds up across releases.

### If an antivirus flags the exe

False positives happen to small new executables. Submit the file for review; it's usually cleared within a few days:

- Microsoft Defender: <https://www.microsoft.com/wdsi/filesubmission> (choose "Software developer")
- Check other engines first at <https://www.virustotal.com>, then use each vendor's false-positive form.

## macOS signing and notarization

This needs an [Apple Developer Program](https://developer.apple.com/programs/) membership (US$99/year).

1. Create a **Developer ID Application** certificate in Xcode or on developer.apple.com. Export it with its private key as a `.p12` file.
2. Create an app-specific password at <https://account.apple.com> → Sign-In and Security.
3. Add these GitHub secrets:
   - `MACOS_CERT_P12`: the `.p12` file, base64-encoded (`base64 -i cert.p12 | pbcopy`)
   - `MACOS_CERT_PASSWORD`: the `.p12` export password
   - `APPLE_ID`: your Apple ID email
   - `APPLE_TEAM_ID`: 10-character team ID
   - `APPLE_APP_PASSWORD`: the app-specific password
4. Push a tag. The workflow signs with the hardened runtime, notarizes, staples and publishes. Users then get no Gatekeeper warning.

To do the same locally:

```bash
SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" ./macos/build.sh
APPLE_ID=… APPLE_TEAM_ID=… APPLE_APP_PASSWORD=… ./macos/notarize.sh
```

## Cutting a release

1. Bump the version in `macos/Info.plist` (`CFBundleShortVersionString` and `CFBundleVersion`), `windows/BSSIDLock/BSSIDLock.csproj` (`Version`, `FileVersion`, `AssemblyVersion`) and `windows/BSSIDLock/app.manifest`.
2. Commit, then `git tag vX.Y.Z && git push origin main vX.Y.Z`.
3. The **Release** workflow builds, signs (if configured), attests and publishes.
