# Data Retention & Erasure Policy

> **Template — confirm periods with legal/HR before adopting.** Replace bracketed values.

## Retention periods

| Data | Retention period | Rationale |
|---|---|---|
| Staff accounts (`UserProfile`) | Duration of employment + `12 months` | Access management; short tail for rehire/disputes |
| Clock-in / shift / rota records | `6 years` | Employment/payroll record-keeping obligations |
| Client device records (IP/MAC) | While the device is in service + `6 months` | Operations & troubleshooting |
| Application/security logs | `90 days` | Security monitoring; data minimisation |

## Current implementation status

The application currently uses **soft-delete only** (`DeleteDate` / `IsActive` flags); rows are
never physically removed. This means:

- There is no automated enforcement of the retention periods above.
- An erasure ("right to be forgotten") request cannot yet be fully satisfied in-app, because
  soft-deleted rows and their personal data remain in the database.

## Required follow-up work (tracked separately)

These are deliberately **not** implemented as part of the security-hardening change because they
are destructive and need their own reviewed, tested change:

1. **Retention sweeper** — a scheduled job that hard-deletes or anonymises records past their
   retention period.
2. **Erasure endpoint** — an admin action that hard-deletes or irreversibly anonymises a data
   subject's personal data on a verified request (name, email, DOB → nulled/anonymised; logs
   scrubbed of the subject's identifiers).
3. **Subject Access Request (SAR) export** — an admin action that exports all personal data held
   about a given staff member.

## Handling a request today (manual process)

Until the above is built, SAR / erasure requests must be handled manually by an administrator
with database access.