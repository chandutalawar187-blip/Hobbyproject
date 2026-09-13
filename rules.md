# Repository Contribution Rules

## Branching

- Keep `main` releasable.
- Create one branch per coherent feature or bug fix:
  - `feature/<short-name>` for new behavior.
  - `fix/<short-name>` for defect repairs.
  - `docs/<short-name>` for documentation-only changes.
- Branch from the latest `main`.
- Keep branches focused and avoid mixing unrelated work.
- Rebase a feature branch onto `main` before opening a pull request when practical.
- Do not force-push shared branches.

## Commits

- Commit meaningful, validated changes promptly.
- Use imperative, specific commit subjects, such as:
  - `Fix Lenovo RGB HID capability detection`
  - `Add live keyboard lighting preview`
- Keep each commit logically reviewable; do not commit generated build output, logs, secrets, or local machine settings.
- Include the required trailer on commits created by Copilot:

  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

- Do not amend published commits unless explicitly requested.

## Validation

- Run the smallest relevant build and test commands before committing.
- For hardware changes, distinguish software validation from physical device confirmation.
- Never claim hardware control succeeded without an explicit device response or user confirmation.
- Preserve capability gates, bounded values, rate limits, and safe unsupported behavior.

## Pull requests and review

- Push feature branches to `origin` and open a pull request into `main`.
- Describe the user-visible change, hardware limitations, validation performed, and any remaining physical-test requirement.
- Keep CI green before merging.
- Prefer squash or clean, logically grouped commits when merging.

## Releases and maintenance

- Tag releases from `main` only after build and test validation.
- Document behavior changes and hardware support changes in `README.md`.
- Remove temporary diagnostics when they are no longer needed, or document their location and purpose.
- Keep dependencies and workflows reviewable; avoid unrelated formatting churn.
