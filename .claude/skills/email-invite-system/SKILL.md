---
name: email-invite-system
description: How LanyardApp sends new-user invite/welcome emails (via Resend's transactional API, not the user's home server) and how login accepts either a username or an email address. Use when working on user invites, welcome/set-password emails, EmailService/EmailOptions, or debugging a login failure that might be username-vs-email related.
---

# Email / Invite System

New-user invites email a welcome/set-password link rather than an admin showing a generated password once. Sending goes through **Resend**'s transactional API via `IEmailService`/`EmailService` in `src/Lanyard.Server/LanyardServices/Services/Email/`, called directly from the hosted app over HTTPS.

## Why Resend and not the home server

The user has a home server and considered routing invite emails through it. That was deliberately ruled out: residential IPs have poor deliverability — ISPs commonly block outbound port 25, and mailbox providers (Gmail, Outlook) spam-filter unknown residential senders regardless of message content. Resend plus DNS-level domain verification (SPF/DKIM/DMARC on the sending domain) solves this properly. **If asked to change or debug email sending, don't suggest routing through the home server without re-raising this deliverability tradeoff** — it was already considered and rejected for a specific reason, not overlooked.

## Where things live

- Service: `IEmailService`/`EmailService` in `src/Lanyard.Server/LanyardServices/Services/Email/`.
- Config: `"Email"` appsettings section, or env vars `Email__ResendApiKey`, `Email__FromAddress`, `Email__FromName`.
- The Resend API key and domain verification are a manual one-time setup step in the Resend + DNS provider dashboards — not something committed to the repo. If email sending breaks in a way that isn't a code bug, check that setup hasn't lapsed (e.g. DNS record changes, API key rotation) before assuming the service code is at fault.

## Related change: login accepts username or email

`AuthController` (`/api/auth/login` and `/api/auth/login-form`) resolves the identifier via `FindUserByUsernameOrEmailAsync`, which tries username first and falls back to email. This exists because the auto-generated username (first-initial + surname) isn't something a newly-invited user reliably remembers — they're more likely to remember the email address the invite arrived at. If debugging a "can't log in" report, check whether the entered value is being matched as a username vs. an email before assuming it's a password/credential issue.
