# GitHub publication and application updates

Repository: **https://github.com/Szeb95/ShockWave**, public source repository owned by **Szeb95**.

First public release: **[ShockWave 0.7.0](https://github.com/Szeb95/ShockWave/releases/tag/v0.7.0)**. The app's **[latest-release link](https://github.com/Szeb95/ShockWave/releases/latest)** is live. The uploaded Windows ZIP is the tested local 0.7.0 package, **116,014,214 bytes**, SHA-256 `f443b7fb11cf8d09e70188379ab9007f2774fc08d16d0ffe1e8fd8316e31f810`.

**0.7.7 update:** automatic SPS now has shared source switches, per-function gears and socket On display, with the pump-selection correction. Connect adds saved friends protected by Windows. Both friends need 0.7.7 or later for saved pairing. See the newest [workstream status](lovense-remote/STATUS.md) entry for package, publication and public-updater verification. Existing preferences and Unity installations are preserved.

## Current state

The published project includes its homepage with original banner and simulated screenshots, setup/build links, contribution/security guidance, issue/PR templates, third-party notices and a Windows source-check workflow. Repository topics and private vulnerability reporting are enabled.

The owner has chosen contribution-only permissions: inspect/build ShockWave and develop fixes to contribute back; no independent modified releases or reuse in unrelated programs without permission. The [ShockWave Source-Available Contribution License 1.0](../LICENSE), effective 2026-10-21, implements those permissions, including a project-limited contributor grant and express preservation of GitHub/platform, statutory and third-party rights. The license is adopted locally. Do not replace it with MIT/GPLv3 or describe these restrictions as standard open source. GitHub identity/access is confirmed as **Szeb95** under the normal Windows account; the earlier restricted-account check could not access that credential. Publication proceeds from a separate audited source snapshot, preserving the original checkout. See the newest publication entry in `lovense-remote/STATUS.md` for actual repository/release results.

The source-check workflow runs on pushes to main/master, pull requests and manual requests. It uses a standard Windows runner, restores locked Node dependencies, runs bridge and updater fixtures and builds the Windows app. Its [first successful hosted run](https://github.com/Szeb95/ShockWave/actions/runs/39658801328) passes all those steps. The initial run exposed a test-only expired-timestamp boundary race; the fixture now uses an unambiguously expired timestamp without changing production behavior. A separate manually invoked **Prepare Windows release** workflow builds/verifies a complete ZIP and creates a draft release; that manual packaging workflow still awaits its first hosted execution. Neither workflow starts the normal app, activates hardware or opens Unity.

## Publication process

1. Include the adopted `LICENSE` and preserve third-party license/notices separately. Keep project and contribution summaries consistent with its contribution-only permissions and GitHub's own public-repository rights.
2. Use the intended **Szeb95** GitHub account and confirm repository access. On this workstation the normal Windows account can access its existing keyring login; the restricted account could not.
3. Review the exact publication file list and run a secret/private-identifier scan. `.gitignore` is an exclusion aid, not proof a file contains no secrets. The initial publication used an isolated 230-file snapshot rather than changing the original checkout's Git metadata. Do not bulk-upload generated work folders or personal data.
4. Include the shared source under `work/ShaderPanelTest`, `work/CurvePanelVRDemo` and `work/ToyControlEditor`: production code references these paths. Keep source and appropriate notices, excluding generated builds/backups/check output.
5. Confirm source build and fixture results, then commit/push reviewed changes in a Git checkout of this repository. The original development folder was preserved during initial publication; its separate publishing checkout is recorded locally in `work/publication-checks/snapshot-path.txt`. Do not claim physical or clean-PC verification from software checks.
6. Configure the About text/topics and enable private vulnerability reporting if supported by the account. Confirm visible links after publication.

Suggested About text: **Windows desktop and VRChat controls for Bluetooth toys, OpenShock and PiShock, with OSC/SPS mappings and direct app connections.**

Suggested topics: `vrchat`, `osc`, `windows`, `winforms`, `openshock`, `pishock`, `bluetooth`, `lovense`.

## Releases

The first updater-enabled public development preview is **0.7.0**, recorded in `lovense-remote/STATUS.md` and published at its matching GitHub tag. Do not overwrite this public asset or archived local ZIPs; use a newer numeric version for each actual update. Do not describe a new package as verified solely because an earlier version passed checks.

The older Inno Setup pipeline predates the current bundled-engine/direct-remote release. Align and verify that pipeline before advertising a current installer: preserve the Bluetooth engine, current remote modules/guides and current avatar-tool files; exclude the proprietary legacy DLL and private data. The existing installer script currently removes the entire staged native directory and has an older publish-root allowlist, so it must not be used unchanged as the updater payload.

A finished release contains a versioned Windows package, checksums, matching source tag, dependency notices and release notes stating actual test evidence. `overlay/package-release.ps1` produces the current ZIP package with `update-manifest.json`; use it instead of the older installer as the update payload. The **Prepare Windows release** workflow builds/checks the package and creates a draft release. The owner publishes that finished release before the updater will offer it. Publishing every code edit directly to users is not configured.

## In-app updates — implemented locally

The app has an **Update** page below Settings and an **Update available** link beside the Toy panel heading. The implemented flow is documented in [UPDATES.md](UPDATES.md):

1. Query the intended repository's latest published compatible stable release.
2. Show the available version and release notes; install only after the user selects **Update & restart**.
3. Download the complete Windows package into staging over HTTPS from the intended repository/GitHub download hosts. Require GitHub's SHA-256 asset digest and verify size, manifest, file hashes, required files, safe paths and EXE version. Trust is the configured GitHub account/repository and HTTPS, not independent Authenticode signing.
4. Use the app's existing close confirmation and cleanup. If the user cancels, abort installation. Wait for owned workers and the installed EXE to exit; do not forcibly replace a running copy.
5. Apply the verified update through a copied helper, preserve `%LOCALAPPDATA%\SzebsToys` and user-added files, back up replacements, attempt rollback on copy failure, and relaunch only on a successful manual update. Automatic update is opt-in: it downloads in the background and installs at normal app close without reopening the app. Preserve Test mode and require fresh input rather than replaying activity.
6. Fixture checks cover missing release, throttling, interrupted/corrupt download, incompatible/downgrade metadata, active-device close cancellation, files in use, install failure/rollback, helper installation and restart intent. Anonymous access to the published latest-release page/API and the app parser's acceptance of actual release metadata are verified. A real installed upgrade to a later version and clean-PC verification remain pending.

The first updater-enabled version requires a manual installation. Unity installation, scene state and avatar uploads stay outside the application update flow.

## GitHub references

- [Repository README files](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes)
- [Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)
- [Latest-release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
- [Standard GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [Actions billing and storage allowances](https://docs.github.com/en/billing/concepts/product-billing/github-actions)

Standard public-repository build runners are free under GitHub's current documented terms. Artifact/cache storage has separate allowances; this workflow uploads no build artifacts or cache. A separate GitHub Pages website can be added later if desired; the repository README provides the initial public project page.

## Creator Companion package and listing

`vpm/package.json` defines `com.szebs.shockwave.avatar-panel`, independently versioned from the Windows app. PowerShell 7 `scripts/package-panel.ps1` creates a root-level VPM ZIP/checksum and adds its manifest plus `zipSHA256` to `vpm/index.json`. Existing GUIDs are preserved; missing metadata is deterministic. The legacy migration names only `Assets\Editor\SzebsToysToyControl`, never generated assets, scenes, packages or settings. VRCFury's official repository must be available in VCC for dependency resolution.

Publish immutable package assets under `panel-v<version>` with `gh release create --latest=false`. Do not let an avatar-only release become latest: the Windows updater reads GitHub's stable latest release. Preserve prior listing versions. Verify the public ZIP digest against the listing and test package-based source resolution in a separate scratch project before publication.

The **Publish Creator Companion listing** workflow serves only `vpm/` through GitHub Pages at https://szeb95.github.io/ShockWave/. Its HTTPS landing page links the VCC custom URI and provides a copyable listing URL. GitHub README links the landing page below A look inside, because GitHub does not reliably allow custom VCC URI links. The package updates tools only; users explicitly Update their avatar and upload. Never test/import the real avatar project or change its Packages/settings automatically.
