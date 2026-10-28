# Messaging: email and SMS

EstateHub sends invitations, sign-in codes and (later) statements and notices. The providers are
**configuration only**: the code talks to `IEmailSender` / `ISmsSender`, and settings under
`Messaging__*` (environment variables or `.env.prod`) pick the service. Moving from development to
the pilot to a paid service never needs a code change or a new release.

## Email: SMTP, whatever the provider

| Stage | Service | Settings |
|---|---|---|
| Development | Mailpit (in `docker-compose.dev.yml`) | already set; open http://localhost:8025 |
| Pilot / MVP | Brevo free tier (300 emails/day), or any SMTP service with a free tier | `SmtpHost=smtp-relay.brevo.com`, `SmtpPort=587`, `SmtpUseTls=true`, `SmtpUser`, `SmtpPassword`, `From` |
| Growth | Amazon SES, SendGrid, Postmark, Mailgun… | the same five settings, from that provider |

Before the first real email: verify the sending domain with the provider (SPF + DKIM DNS records),
or messages land in spam.

## SMS: one adapter per provider

`Messaging__SmsProvider` chooses the adapter. Only South African numbers (+27…) are ever sent to
(AUTH-06), and message text is never logged (it carries codes and links).

| Value | Use | Settings |
|---|---|---|
| `mailpit` | Development: each SMS arrives in Mailpit as an email to `sms-27821234567@sms.estatehub.local` | none |
| `android-gateway` | Pilot: the free "SMS Gateway for Android" app (sms-gate.app) on a phone with a SIM; costs only the SIM's bundle | `AndroidGateway__Url` (the app's send endpoint, e.g. `http://<phone-ip>:8080/message`), `AndroidGateway__Username`, `AndroidGateway__Password`. Check the endpoint against the app version installed when setting it up |
| `bulksms` | Paid, pay-as-you-go, SA delivery | `BulkSms__TokenId`, `BulkSms__TokenSecret` (an API token from bulksms.com) |
| `none` (default) | Production until one is chosen: SMS sends are refused with a clear error; email still works | — |

**Adding a provider** (SMSPortal, Clickatell, …): one more `case` in
`Infrastructure/Messaging/Messaging.cs` that posts to the provider's API (about 30 lines), plus its
settings class. Nothing else changes.

## Costs are capped in code

Codes: 5 per address and 20 per IP per hour (10 SMS per IP). Invitations: 100 per estate per hour,
5 sends per invitation, one per minute (PHASE1.md AUTH-06). These hold whichever provider is used.
