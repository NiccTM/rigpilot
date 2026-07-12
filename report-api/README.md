# PC Helper compatibility report API

This Cloudflare Worker accepts only explicit, user-approved, client-redacted compatibility reports.

## Privacy and retention

- Maximum request size: 2 MB.
- Compressed requests, sensitive keys/text, network inventory, and unbounded arrays are rejected server-side.
- Raw JSON is stored in R2 for 30 days using R2 encryption at rest.
- Redacted summary rows are retained in D1 for one year; a weekly Worker cron removes expired rows.
- The response returns a 256-bit deletion token. `DELETE /v1/reports/{id}` removes the raw object and summary.
- No report upload is performed by the privileged PC Helper service.

## Deployment

1. Create the R2 bucket and D1 database named in `wrangler.jsonc`.
2. Replace the D1 database ID.
3. Apply `migrations/0001_initial.sql`.
4. Set `REPORT_HASH_SALT` with `wrangler secret put REPORT_HASH_SALT`.
5. Configure an R2 lifecycle rule to delete `v1/` objects after 30 days.
6. Run `npm run check`, `npm test`, and `npm run deploy`.

The worker configuration is intentionally incomplete until real Cloudflare account identifiers and a domain are supplied. Local app development does not require this service.
