# Security policy

## Supported versions

NucleoDrive is developed on `main`. Security fixes go into `main` and the next release. Older releases don't get
fixes, so please update to the latest one.

## Reporting a vulnerability

**Please don't open a public issue for security problems.**

Report them privately on GitHub: open the repository's **Security** tab and choose **Report a vulnerability**
([direct link](https://github.com/m411s/nucleodrive/security/advisories/new)). Only the maintainer can see the report.

Please include:

- what an attacker could do, and what they need for it (e.g. a crafted file name on Drive, a shared folder,
  another user on the same computer);
- steps to reproduce, and the NucleoDrive version (Settings › About) and distribution;
- a proof of concept if you have one. Use a test account, never someone else's data.

What to expect: NucleoDrive is a volunteer project, so these are goals, not guarantees.

- an acknowledgement within 7 days;
- an assessment and a plan within 14 days;
- a fix released as soon as practical, and a GitHub security advisory crediting you (unless you'd rather
  not be named).

Please give us a reasonable time to release a fix before you disclose the issue publicly. 90 days is the
default; tell us if you need a different timeline.

## Scope

In scope: NucleoDrive's own code in this repository. That covers the app, the background service, `nucleodrivectl`,
the sync engine, `install.sh`, `uninstall.sh`, the packages, and the GitHub workflows. Examples:

- content on Drive (names, folder structures, shared folders) making NucleoDrive read, write, move or delete
  anything outside your sync folder, or run anything;
- another user on the same computer controlling the service or reading your configuration;
- NucleoDrive installing or running an unverified program.

Out of scope, please report these elsewhere:

- **Proton Drive, Proton's servers and the `proton-drive` CLI**: report them to Proton through its
  [bug bounty programme](https://proton.me/security/bug-bounty);
- problems that need malware already running as your user, or root access.

## How NucleoDrive protects you

- Your Proton password never passes through NucleoDrive. You sign in in your browser, and the CLI keeps the
  session in your system keyring.
- Files are encrypted by Proton's CLI on your computer before upload.
- The CLI is run with an argument list, never through a shell, and file names are passed after `--`, so
  a name like `--help` can never be read as an option.
- Sync never follows or writes through symbolic links, and never writes outside the sync folder.
- Deletions go to the Drive trash or your desktop Trash, and large deletions wait for your confirmation.
- Settings and state are readable only by you, and the control socket refuses other users.
- The CLI is downloaded only from proton.me and checked against Proton's published SHA-512 checksum.
- The background service runs with systemd hardening, and GitHub Actions are pinned to exact commits.

The full threat model, past findings and planned improvements are in
[`nucleodrive-net/docs/SECURITY.md`](nucleodrive-net/docs/SECURITY.md). What NucleoDrive stores and sends is described in
[PRIVACY.md](PRIVACY.md).
