# Code signing policy

## Status and provider

ProtectedApp plans to apply for code signing for its public releases through
the SignPath Foundation open-source program. Once the application is approved,
official releases will carry the following statement: **Free code signing
provided by SignPath.io, certificate by SignPath Foundation.**

Until that approval exists, no binary signed with a local development
certificate is an official release or may be distributed as one.

## Responsibilities

- **Author, maintainer, committer, and reviewer:**
  [@valvik77](https://github.com/valvik77). This person may modify the code and
  reviews external contributions.
- **Signing approver:** [@valvik77](https://github.com/valvik77). Every signing
  request for a release requires their manual approval.
- Contributions from people without write permission are made by pull request
  and reviewed before merging into `main`.

Changes are reviewed through pull requests whenever practical. Releases are
created only from a reviewed, tagged revision. Artifacts must originate from
GitHub-hosted Actions, pass build, test, and dependency-audit checks, and retain
verifiable provenance.

## Signing scope

Only first-party binaries built from this official repository are signed.
Third-party binaries are never signed with a project certificate. Published
releases include release notes, SHA-256 hashes, and, when available, the
corresponding Authenticode verification information.

Private keys for public or release signing are not stored in Git, CI artifacts,
or development machines. Local development signing uses a self-signed
certificate with a private key in the user's certificate store only for testing;
that certificate is excluded from Git and its binaries are not official
releases. Signing configuration, certificate rotation, and update validation
are documented in [SIGNING.en.md](SIGNING.en.md).

## Privacy and network communications

ProtectedApp sends no telemetry or user information over the network. The
installer or operator may explicitly configure the optional tamper-alert
webhook; that HTTPS webhook sends only the data described in the README. When
validating the Authenticode signature of a manually selected update, Windows
may query the certificate issuer's revocation services (OCSP or CRL), depending
on the system's networking and security configuration. Updates are selected
from a local file; the application neither downloads nor installs them
automatically.

## Reporting issues

Report vulnerabilities privately according to [SECURITY.en.md](SECURITY.en.md).
Fixes and releases are not signed until they have passed the applicable security
checks and reviews.
