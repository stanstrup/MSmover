# The unsigned-publisher warning

MSmover's binaries are not code-signed, so Windows shows a SmartScreen warning the first time you
run the installer:

> **Windows protected your PC** — Microsoft Defender SmartScreen prevented an unrecognised app from
> starting. Publisher: Unknown publisher.

**More info → Run anyway** gets past it. Every release publishes a `.sha256` beside each download
so you can confirm you have the file that was built:

```powershell
(Get-FileHash MSmover-0.1.0-win-x64-setup.exe -Algorithm SHA256).Hash
```

The rest of this page is about making the warning go away properly.

> [!IMPORTANT]
> There is no free trick that removes the warning for the general public. Anything that claims
> otherwise is either signing (which costs money or requires an approved open-source programme) or
> suppressing the warning on machines you control. Both are legitimate; they solve different
> problems.

## If you only need it gone on your own instrument PCs

This is the common case for a lab, and it is genuinely straightforward.

### Unblock the file

SmartScreen triggers on the Mark-of-the-Web, an alternate data stream Windows attaches to
downloads. Removing it removes the warning:

```powershell
Unblock-File .\MSmover-0.1.0-win-x64-setup.exe
```

Or right-click the file → **Properties** → tick **Unblock** → OK.

Distributing the installer over a network share rather than a browser download usually avoids the
mark being applied at all.

### Or trust your own certificate

If IT manages the instrument PCs, a self-signed code-signing certificate costs nothing and works
completely — on those machines:

```powershell
# once, on a machine you control
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject "CN=<your group name>" -CertStoreLocation Cert:\CurrentUser\My
Export-Certificate -Cert $cert -FilePath group-codesign.cer
Export-PfxCertificate -Cert $cert -FilePath group-codesign.pfx -Password (Read-Host -AsSecureString)
```

Import `group-codesign.cer` into **Trusted Publishers** (and **Trusted Root Certification
Authorities**) on each instrument PC, by Group Policy or by hand, then sign releases with the
`.pfx` — see [signing your own builds](#signing-your-own-builds).

A self-signed certificate means nothing to anyone outside your organisation. Inside it, it is a
complete answer.

## If you want it gone for everyone

You need a certificate from a CA that Windows already trusts.

| Route | Cost | Warning gone immediately? |
|---|---|---|
| **SignPath Foundation** — free certificates for open-source projects | Free, subject to their acceptance criteria | No — SmartScreen reputation still accrues |
| **Azure Trusted Signing** — Microsoft's signing service | About $10/month plus an Azure subscription | No, but reputation builds quickly |
| **OV certificate** from a commercial CA | Roughly €200–400/year, plus a hardware token | No — reputation still has to accrue |
| **EV certificate** from a commercial CA | Roughly €400–700/year, hardware token required | **Yes** — EV grants SmartScreen reputation from the first signature |

Two things worth knowing before you spend anything:

- **Signing and SmartScreen are separate problems.** A signature tells Windows *who* published the
  file. SmartScreen additionally asks whether that publisher is *known*, which for OV certificates
  means accumulating downloads without incident. So an OV certificate changes "Unknown publisher"
  to your name but may not remove the warning straight away. Only EV skips the reputation phase.
- **Private keys must live in hardware.** Since 2023 the CA/Browser Forum requires code-signing
  keys to be held on a FIPS 140-2 Level 2 device or an equivalent cloud service, so a certificate
  is no longer just a file you download. Budget for the token or the service.

For an MIT-licensed project like this, **SignPath Foundation** is the obvious first thing to try:
it is free, it is designed for exactly this case, and its certificates are trusted everywhere.
**Azure Trusted Signing** is the cheapest paid route and integrates cleanly with GitHub Actions.

Also worth a five-minute check: your institution may already hold a code-signing certificate that
central IT can sign artefacts with.

## Signing your own builds

The release pipeline already supports it. Nothing is enabled by default; supply a certificate and
signing happens automatically.

1. Base64-encode your `.pfx`:

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes('group-codesign.pfx')) | Set-Clipboard
   ```

2. Add two repository secrets:

   | Secret | Value |
   |---|---|
   | `MSMOVER_SIGN_PFX_BASE64` | the base64 string |
   | `MSMOVER_SIGN_PASSWORD` | the `.pfx` password |

3. That is all. The next release is signed.

Locally, the same two environment variables do the same thing:

```powershell
$env:MSMOVER_SIGN_PFX_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))
$env:MSMOVER_SIGN_PASSWORD   = 'secret'
powershell -File build\package.ps1 -Version 0.0.0-local
```

Without them the build still succeeds and prints a warning that the binaries are unsigned.

### What gets signed, and in what order

`build\package.ps1` signs the application executable **before** the installer is built, then signs
the installer, then writes the checksums. That ordering matters: signing only the installer would
leave an unsigned `MSmover.exe` on disk after installation, which is what Windows actually runs.
Signatures are timestamped (RFC 3161), so they remain valid after the certificate expires.

## What is not a solution

- **Disabling SmartScreen.** It protects against a real class of problem; turning it off machine-wide
  to install one utility is a bad trade.
- **A `.zip` instead of an installer.** The mark-of-the-web propagates to extracted files.
- **Waiting.** Reputation accrues per signing certificate, not per unsigned binary. An unsigned
  executable never stops warning, however many people run it.
