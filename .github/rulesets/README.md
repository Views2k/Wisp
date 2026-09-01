# Repository rules

`protect-main.json` and `protect-release-tags.json` are the reviewed ruleset
definitions for Wisp's default branch and `v*` release tags. Keeping the
definitions in the repository makes the intended policy auditable, but these
files do not enforce anything by themselves.

Wisp's public repository can use repository rulesets on GitHub Free, but these
checked-in definitions still do not enforce anything until an administrator
imports and activates them. `CODEOWNERS` records review ownership; enforceable
review requirements come from the active ruleset.

When the repository and account plan support rulesets, an administrator must
import both JSON files under **Settings > Rules > Rulesets**, leave their
enforcement set to **Active**, and compare the imported rules with the files in
this directory.

`protect-main.json`:

- blocks deletion, force pushes, and merge commits;
- requires pull requests and resolution of review conversations;
- permits the solo maintainer to merge a green pull request without a second
  reviewer; and
- requires the `Build and test` and `Build installer` status checks against the
  latest `main`.

`protect-release-tags.json`:

- applies to tags matching `v*`;
- permits an initial matching tag to be created by a repository writer;
- blocks updates, deletion, and non-fast-forward changes after creation; and
- grants no bypass access.

After import, verify both active rulesets under
**Settings > Rules > Rulesets > Rule insights**.

Tag rules protect Git references, not GitHub Release records or uploaded
assets. Repository collaborators with write access can edit releases. Keep
write access owner-only when release publication must remain owner-controlled,
or use an organization repository with a narrower role for contributors.
