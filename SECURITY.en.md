# Security policy

> **English** | [Español](SECURITY.md)

## Supported versions

Only the latest published stable version receives security fixes.

## Reporting a vulnerability

Do not publish vulnerabilities, working proof-of-concepts, or exploitation
details in public issues. Use GitHub's **Report a vulnerability** option from
the repository's Security tab to send a private report to the maintainer.

Include the impact, the minimum reproduction steps, the affected version, and,
where possible, a proposed fix. Do not include passwords, real vaults, private
keys, tokens, or third-party data.

The goal is to acknowledge the report, assess impact, prepare a fix, and publish
a signed release before disclosing details.

## Security-model limits

ProtectedApp protects applications and vaults from accidental or unauthorized use
in a normal Windows session. It is not a boundary against a local administrator
or `SYSTEM`. Use it with BitLocker, Secure Boot, Windows accounts, backups, and,
in managed environments, App Control for Business/WDAC.
