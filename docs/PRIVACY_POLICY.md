# Privacy Policy

> **Filled in from known operational details — still get a final legal read before relying on
> this in a dispute.** This document reflects the personal data Lanyard actually processes.
> Last updated: 20 October 2026. **The October 2026 changes (staff documents, chat, time off,
> notifications, passkeys, processors and cookie lifetimes) are awaiting sign-off by the data
> controller.**

## 1. Who we are

Play2Day ("we", "us") operates the Lanyard system to run our entertainment venue(s). For the
purposes of UK GDPR / EU GDPR, the data controller is **Benjamin Mercer**. Our contact for data
protection matters is **Benjamin Mercer, benmercer76@btinternet.com**.

## 2. What personal data we process

| Category | Data | Where |
|---|---|---|
| Staff account | Username, email, first/last name, date of birth, preferred language, hashed password | `UserProfile` |
| Staff attendance | Clock-in/out times, shifts, rota assignments | ClockIn / Shift / Rota records |
| Device/network | Client device IP addresses, MAC/physical addresses of network interfaces | `Client`, network interface records |
| Staff documents | Right-to-work checks, identity documents, DBS checks, certificates, with their expiry and reminder dates | `StaffDocument` rows and the uploaded files |
| Training | Course assignments, progress, quiz attempts and certificates | Course assignment records |
| Time off and availability | Leave requests (including the reason given), allowances, when someone can or would rather not work | Time off and availability records |
| Chat | Messages, reactions, read receipts, blocks, and reports of messages (with a copy of the reported message) | Chat records |
| Feedback | Ratings and comments sent about Lanyard, with the page they were sent from | `UserFeedback` |
| Sign-in security | Two-factor authentication settings and recovery codes (hashed), passkeys (public key and device name), and a record of when sign-ins and security changes happened | Identity records |
| Notifications | Push notification subscriptions for each device, the app installations a person uses, and their notification preferences | Notification records |
| Technical logs | IP addresses and activity in server logs and telemetry | Application logs, Seq |

We do **not** ask for special-category data, but staff documents can contain it: an identity
document or right-to-work check may show nationality, and a DBS check shows criminal-record
information. These files are kept private to Admins and the person's Managers, are never shown
in the File Manager, and are deleted when the person's data is erased. Note that **date of
birth** is collected for staff; if any data subjects are minors, additional safeguards apply.

## 3. Why we process it and our legal basis

| Purpose | Legal basis (UK/EU GDPR Art. 6) |
|---|---|
| Managing staff accounts and access control | Contract / legitimate interests |
| Recording working time (clock-in, shifts, rota) | Legal obligation (employment records) / contract |
| Checking the right to work, identity, DBS and qualifications (staff documents) | Legal obligation (right-to-work checks) / contract; criminal-record data under the relevant Schedule 1 condition of the Data Protection Act 2018 |
| Training, time off, chat and notifications | Contract / legitimate interests |
| Operating and securing venue devices and the network | Legitimate interests |
| Security logging and abuse prevention | Legitimate interests |

## 4. How long we keep it

Personal data is retained only as long as necessary for the purposes above. See
[`DATA_RETENTION.md`](DATA_RETENTION.md) for specific retention periods and the erasure process.

## 5. Who we share it with

We do not sell personal data. We share it only with:

- Hosting/infrastructure providers acting as our processors (**Railway**, hosted in the West
  Europe region): the database and the storage bucket that holds uploaded files, including staff
  documents.
- **Resend**, which sends Lanyard's emails (invitations, password resets, sign-in codes and
  reminders). It receives the recipient's name, email address and the email's content.
- Browser push services (for example Google, Mozilla and Apple), which deliver push
  notifications to devices a person has turned them on for. They receive the notification text
  and a device address, not the person's account details.
- Application logs and telemetry are sent to a **Seq** server that we host ourselves.
- Authorities where required by law.

## 6. Where it is stored

Personal data is stored in our PostgreSQL database hosted on **Railway, West Europe region**.
Uploaded files, including staff documents, are stored in a Railway storage bucket in the same
region. The database and files do not leave the UK/EU. Emails sent through Resend and push
notifications delivered through browser push services may be processed outside the UK/EEA by
those providers.

## 7. Your rights

Subject to law, you have the right to access, rectify, erase, restrict, or object to processing
of your personal data, and to data portability. To exercise these rights, contact
**Benjamin Mercer, benmercer76@btinternet.com**. You also have the right to complain to the
**Information Commissioner's Office (ICO)**.

## 8. Cookies

Lanyard uses only strictly-necessary cookies (for authentication and session security). See the
[Cookie Notice](#cookie-notice) below.

### Cookie Notice

| Cookie | Purpose | Type | Expiry |
|---|---|---|---|
| `.AspNetCore.Identity.Application` | Keeps you signed in | Strictly necessary | Until the browser closes; with **Keep me signed in** ticked, 90 days from your last visit (sliding) |
| `Identity.TwoFactorRememberMe` | Skips the two-factor code on a device you chose to remember | Strictly necessary | 30 days |
| `.AspNetCore.Culture` | Remembers your language when you sign in | Strictly necessary | 1 year |
| Antiforgery token | Protects forms against CSRF | Strictly necessary | Session |

Because these cookies are strictly necessary for the service to function, they do not require
consent, but we tell you about them here for transparency. We do not use analytics, advertising,
or tracking cookies. [If that changes, a consent banner must be added before such cookies are set.]

## 9. Changes

We may update this policy; the "last updated" date reflects the latest revision.
