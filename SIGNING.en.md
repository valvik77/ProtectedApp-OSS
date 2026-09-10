# Signing open-source releases

> **English** | [Español](SIGNING.md)

## Moving to a different certificate

The automatic updater requires the same certificate thumbprint and rejects new
certificates, including renewals. Until a transition is authenticated by the
previous signer, changing the certificate requires a reinstall:

1. Save and lock vaults; export the encrypted settings backup and keep vault
   containers and backups outside ProtectedApp's local folder.
2. Verify that those backups can be restored before uninstalling.
3. Uninstall through the official uninstaller and master password.
4. Verify the publisher of the new installer, install it, and restore settings.

Do not manually remove the protected thumbprint or accept any valid certificate
as a replacement. Automatic SignPath integration remains pending until its
public signing identity is known and approved.

Development binaries may be signed with a local certificate for testing only.
They must not be publicly distributed or added to the repository.

For public releases, ProtectedApp applies for an Open Source signing subscription
with SignPath Foundation after publishing the repository and meeting its project
conditions. The GitHub integration will be configured by SignPath with minimum
permissions and a release branch/tag restriction.

## Security rules

- Never store certificates, private keys, passwords, or signing tokens in Git,
  Actions artifacts, or variables visible in logs.
- Sign only CI artifacts generated from a protected release tag and reviewed
  revision.
- Verify Authenticode, the signature chain, timestamp, and SHA-256 hash before
  publishing a release.
- Publish the installer, its hashes, and release notes through GitHub Releases;
  never replace files under the same tag.

The specific SignPath configuration is not included until the Foundation approves
the request and provides a project identifier. This avoids a template containing
fictional credentials or excessive permissions.
