# Security Policy

## Reporting a Vulnerability

We take the security of Lanyard seriously. If you believe you have found a security
vulnerability, please report it privately — **do not open a public GitHub issue.**

Preferred channels:

1. **GitHub private vulnerability reporting** — use the "Report a vulnerability" button under
   the repository's **Security** tab.
2. **Email** — `benmercer76@btinternet.com`

Please include:

- A description of the vulnerability and its impact.
- Steps to reproduce (proof-of-concept if possible).
- Affected component (Lanyard Server, Client, or Reach) and version/commit.

We aim to acknowledge reports within **5 working days** and to provide a remediation timeline
after triage. Please give us a reasonable opportunity to fix the issue before any public
disclosure.

## Scope

This policy covers the Lanyard Server, Lanyard Client, and Lanyard Reach applications in this
repository.

## Handling of Secrets

- Database credentials and other secrets are supplied at runtime via environment variables
  (e.g. `ConnectionStrings__DefaultConnection`, `Clients__SharedSecret`, `Seed__AdminPassword`)
  and must **never** be committed to the repository.
- If a secret is ever committed, treat it as compromised: rotate it immediately and scrub it
  from history.
