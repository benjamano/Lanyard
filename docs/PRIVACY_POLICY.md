# Privacy Policy

> **Template — review with a legal advisor before publishing.** This document is a starting
> point that reflects the personal data Lanyard actually processes. Replace the bracketed
> placeholders (`[…]`) and confirm the legal bases and retention periods with your DPO/legal
> counsel. Last updated: [DATE].

## 1. Who we are

Play2Day ("we", "us") operates the Lanyard system to run our entertainment venue(s). For the
purposes of UK GDPR / EU GDPR, the data controller is **[Legal entity name, address]**. Our
contact for data protection matters is **[privacy@play2day.com / DPO name]**.

## 2. What personal data we process

| Category | Data | Where |
|---|---|---|
| Staff account | Username, email, first/last name, date of birth, preferred language, hashed password | `UserProfile` |
| Staff attendance | Clock-in/out times, shifts, rota assignments | ClockIn / Shift / Rota records |
| Device/network | Client device IP addresses, MAC/physical addresses of network interfaces | `Client`, network interface records |
| Technical logs | IP addresses and activity in server logs | Application logs |

We do **not** intentionally collect special-category data. Note that **date of birth** is
collected for staff; if any data subjects are minors, additional safeguards apply.

## 3. Why we process it and our legal basis

| Purpose | Legal basis (UK/EU GDPR Art. 6) |
|---|---|
| Managing staff accounts and access control | Contract / legitimate interests |
| Recording working time (clock-in, shifts, rota) | Legal obligation (employment records) / contract |
| Operating and securing venue devices and the network | Legitimate interests |
| Security logging and abuse prevention | Legitimate interests |

## 4. How long we keep it

Personal data is retained only as long as necessary for the purposes above. See
[`DATA_RETENTION.md`](DATA_RETENTION.md) for specific retention periods and the erasure process.

## 5. Who we share it with

We do not sell personal data. We share it only with:

- Hosting/infrastructure providers acting as our processors (e.g. `[hosting provider]`).
- Authorities where required by law.

## 6. Where it is stored

Personal data is stored in our PostgreSQL database hosted at **[region/provider]**. [State
whether any transfers occur outside the UK/EEA and the safeguards used.]

## 7. Your rights

Subject to law, you have the right to access, rectify, erase, restrict, or object to processing
of your personal data, and to data portability. To exercise these rights, contact
**[privacy@play2day.com]**. You also have the right to complain to the **[ICO]**.

## 8. Cookies

Lanyard uses only strictly-necessary cookies (for authentication and session security). See the
[Cookie Notice](#cookie-notice) below.

### Cookie Notice

| Cookie | Purpose | Type | Expiry |
|---|---|---|---|
| `.AspNetCore.Identity.Application` | Keeps you signed in | Strictly necessary | Up to 14 days (sliding) |
| Antiforgery token | Protects forms against CSRF | Strictly necessary | Session |

Because these cookies are strictly necessary for the service to function, they do not require
consent, but we tell you about them here for transparency. We do not use analytics, advertising,
or tracking cookies. [If that changes, a consent banner must be added before such cookies are set.]

## 9. Changes

We may update this policy; the "last updated" date reflects the latest revision.
