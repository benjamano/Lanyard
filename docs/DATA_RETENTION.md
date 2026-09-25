# Data Retention & Erasure Policy

> **Filled in from known operational details — still get a final legal/HR read before adopting.**
> Last updated: 6 August 2026. Data controller / contact: Benjamin Mercer,
> benmercer76@btinternet.com. Hosted on Railway (West Europe region); data does not leave the
> UK/EU.

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

### Rota and timesheet records

Shifts (`Shift`) and clocked time (`TimeEntry`) are kept, not deleted, when a staff account is
deleted or erased, because of the 6-year payroll retention above. Both delete paths
(`SecurityService.DeleteUserAsync` and the GDPR erasure in `GdprService`) run
`ScheduleRetention.DetachUserAsync` first, which:

- re-points the person's shifts and time entries to the seeded placeholder account, so the
  records no longer identify them;
- cancels any shifts they had in the future, and closes any time entry they were still clocked
  in on (flagged for review);
- scrubs their user id from "created / updated / published / approved by" attribution fields.

Clock-in terminals store only a SHA-256 hash of each device's token; clock-in PINs are stored as
salted hashes and deleted with the account. There is still no automated purge once the 6 years
have passed - that remains part of the retention sweeper below.

### Notification emails

Rota, shift-reminder and time-off emails are queued in memory and sent by a background worker;
nothing about them is stored, and a queued email is lost if the app restarts before it goes. The
only record kept is `Shift.ReminderSentForStartUtc`, which stops a reminder being sent twice.
Emails carry names, dates, shift times and any reason a manager gave - nothing more sensitive.

### Open shifts, call-offs and swaps

`ShiftClaims` records who asked to pick up, call off or swap a shift, the manager's decision and
any reason given. It is rota history like the shifts themselves, so it is **kept and anonymised**
with them when an account is deleted (`ScheduleRetention`): the person's claims are re-pointed to
the placeholder account and any still waiting are withdrawn; their name is cleared from decisions
they made. Reasons are free text; managers and staff should avoid putting medical detail in them.
`LocationSchedulingSettings` holds one switch per location and no personal data.

### Push notifications and notification settings

Push notifications carry the same content as the matching email, encrypted end to end: the browser
vendor's push service (Google, Apple, Mozilla) relays them but cannot read them. Three tables hold
per-person data, all **deleted with the account** (cascading foreign keys; removed explicitly by
GDPR erasure):

- `PushSubscriptions` - one row per device where the person switched notifications on: the push
  service address, the two encryption keys the browser gave us, a device label ("Chrome on
  Android") and when it was last used. A row goes when the person signs out on that device, turns
  notifications off, removes it from their device list, when the push service says it no longer
  exists, after five failed sends in a row, or **after 90 days without Lanyard being opened on that
  device** (daily sweep, `PushDeviceCleanupHostedService`).
- `AppInstallations` - one row per device where Lanyard is used as an installed app, with a device
  label and first/last seen dates, so a manager can see who will get alerts. Also removed after 90
  days unseen.
- `NotificationPreferences` - the person's push/email choice per topic, only where it differs from
  the default.

Snoozes for the "Add to your Home Screen" prompt and a random device id are kept in the browser's
own storage, not on the server.

### Time off

Time-off requests (`TimeOffRequest`) and personal allowance overrides (`TimeOffAllowance` rows
with a `UserId`) are the person's own records rather than payroll history - hours actually worked
are in `TimeEntry` - so they are **deleted with the account** (a cascading foreign key on a plain
delete; removed explicitly by GDPR erasure). Where the person decided or recorded someone else's
time off, erasure keeps the decision but clears their name from it. Requests can carry a free-text
reason for a rejection and notes from the requester; managers should avoid putting medical detail
in either. Leave types and company or position allowances contain no personal data.

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