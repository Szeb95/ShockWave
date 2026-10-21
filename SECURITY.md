# Security reports

Do not disclose a vulnerability, real API key, invitation secret or private device identifier in a public issue.

Use GitHub's **Report a vulnerability** option on this repository's Security tab when private vulnerability reporting is enabled. If it is unavailable, open a public issue containing only a request for a private reporting channel; keep reproduction details and sensitive data private until a channel is agreed.

A useful private report includes the affected version, attack prerequisites, steps to reproduce using fake devices/credentials where possible, expected impact and any suggested fix. Do not activate someone else's hardware or use an invitation without their authorization to demonstrate an issue.

ShockWave is currently a development preview. No long-term support window or guaranteed response time has been established. Maintainers should assess fixes against the latest source and identify affected release versions explicitly.

## Data to keep private

- OpenShock/PiShock API credentials and encrypted credential files.
- Connection codes and invitation secrets, even in screenshots.
- Bluetooth addresses, private hardware IDs and user configuration.
- Logs or diagnostic exports containing personal information.

If a credential has leaked, revoke or rotate it with its provider. Deleting a public issue or commit does not invalidate a leaked credential.
