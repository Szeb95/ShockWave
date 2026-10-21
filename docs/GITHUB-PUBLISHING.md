# GitHub publication and application updates

Target: **https://github.com/Szeb95/ShockWave**, public source repository owned by **Szeb95**.

## Current state

Local publication preparation includes a project homepage with original banner and simulated screenshots, setup/build links, contribution/security guidance, issue/PR templates, third-party notices and a Windows source-check workflow.

The owner has chosen contribution-only permissions: inspect/build ShockWave and develop fixes to contribute back; no independent modified releases or reuse in unrelated programs without permission. The [ShockWave Source-Available Contribution License 1.0](../LICENSE), effective 2026-10-21, implements those permissions, including a project-limited contributor grant and express preservation of GitHub/platform, statutory and third-party rights. The license is adopted locally. Do not replace it with MIT/GPLv3 or describe these restrictions as standard open source. GitHub identity/access is confirmed as **Szeb95** under the normal Windows account; the earlier restricted-account check could not access that credential. Publication proceeds from a separate audited source snapshot, preserving the original checkout. See the newest publication entry in `lovense-remote/STATUS.md` for actual repository/release results.

The source-check workflow runs on pushes to main/master, pull requests and manual requests. It uses a standard Windows runner, restores locked Node dependencies, runs bridge and updater fixtures and builds the Windows app. A separate manually invoked **Prepare Windows release** workflow builds/verifies a complete ZIP and creates a draft release. Neither workflow starts the normal app, activates hardware or opens Unity. Neither workflow has run on GitHub yet.

## Before the first public push

1. Include the adopted `LICENSE` and preserve third-party license/notices separately. Keep project and contribution summaries consistent with its contribution-only permissions and GitHub's own public-repository rights.
2. Connect the intended GitHub account and confirm repository availability.
3. Review the exact publication file list and run a secret/private-identifier scan. `.gitignore` is an exclusion aid, not proof a file contains no secrets. The current checkout has no existing commits; do not bulk-upload generated work folders or personal data.
4. Include the shared source under `work/ShaderPanelTest`, `work/CurvePanelVRDemo` and `work/ToyControlEditor`: production code references these paths. Keep source and appropriate notices, excluding generated builds/backups/check output.
5. Confirm source build and fixture results, then create the public repository and initial commit. Do not claim physical or clean-PC verification from software checks.
6. Configure the About text/topics and enable private vulnerability reporting if supported by the account. Confirm visible links after publication.

Suggested About text: **Windows desktop and VRChat controls for Bluetooth toys, OpenShock and PiShock, with OSC/SPS mappings and direct app connections.**

Suggested topics: `vrchat`, `osc`, `windows`, `winforms`, `openshock`, `pishock`, `bluetooth`, `lovense`.

## Releases

The updater-enabled local portable preview is **0.7.0**, recorded in `lovense-remote/STATUS.md`. It is not yet a GitHub release. Do not overwrite archived local ZIPs or describe a new package as verified solely because an earlier version passed checks.

The older Inno Setup pipeline predates the current bundled-engine/direct-remote release. Align and verify that pipeline before advertising a current installer: preserve the Bluetooth engine, current remote modules/guides and current avatar-tool files; exclude the proprietary legacy DLL and private data. The existing installer script currently removes the entire staged native directory and has an older publish-root allowlist, so it must not be used unchanged as the updater payload.

A finished release contains a versioned Windows package, checksums, matching source tag, dependency notices and release notes stating actual test evidence. `overlay/package-release.ps1` produces the current ZIP package with `update-manifest.json`; use it instead of the older installer as the update payload. The **Prepare Windows release** workflow builds/checks the package and creates a draft release. The owner publishes that finished release before the updater will offer it. Publishing every code edit directly to users is not configured.

## In-app updates — implemented locally

The app has an **Update** page below Settings and an **Update available** link beside the Toy panel heading. The implemented flow is documented in [UPDATES.md](UPDATES.md):

1. Query the intended repository's latest published compatible stable release.
2. Show the available version and release notes; install only after the user selects **Update & restart**.
3. Download the complete Windows package into staging over HTTPS from the intended repository/GitHub download hosts. Require GitHub's SHA-256 asset digest and verify size, manifest, file hashes, required files, safe paths and EXE version. Trust is the configured GitHub account/repository and HTTPS, not independent Authenticode signing.
4. Use the app's existing close confirmation and cleanup. If the user cancels, abort installation. Wait for owned workers and the installed EXE to exit; do not forcibly replace a running copy.
5. Apply the verified update through a copied helper, preserve `%LOCALAPPDATA%\SzebsToys` and user-added files, back up replacements, attempt rollback on copy failure, and relaunch only on a successful manual update. Automatic update is opt-in: it downloads in the background and installs at normal app close without reopening the app. Preserve Test mode and require fresh input rather than replaying activity.
6. Fixture checks cover missing release, throttling, interrupted/corrupt download, incompatible/downgrade metadata, active-device close cancellation, files in use, install failure/rollback, helper installation and restart intent. Live GitHub delivery and clean-PC verification remain pending the first public releases.

The first updater-enabled version requires a manual installation. Unity installation, scene state and avatar uploads stay outside the application update flow.

## GitHub references

- [Repository README files](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes)
- [Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)
- [Latest-release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
- [Standard GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
- [Actions billing and storage allowances](https://docs.github.com/en/billing/concepts/product-billing/github-actions)

Standard public-repository build runners are free under GitHub's current documented terms. Artifact/cache storage has separate allowances; this workflow uploads no build artifacts or cache. A separate GitHub Pages website can be added later if desired; the repository README provides the initial public project page.
