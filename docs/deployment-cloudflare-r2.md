# File storage on Cloudflare R2

The API stores uploaded files (photos of lots, cycles, water measurements, certifications…) through `IFileStorage`.
`Storage:Provider=R2` keeps them in a **private** Cloudflare R2 bucket through R2's S3-compatible API. The browser reads a file
through a pre-signed `GET` link that expires after 10 minutes. The bucket is never public and the API never creates it.

## 1. Create the bucket and the token (once)

With the Cloudflare dashboard: **R2 Object Storage → Create bucket** `omts-files` (Location: Automatic, Asia-Pacific hint).
Then **R2 → Manage R2 API Tokens → Create API token**:
- Permissions: **Object Read & Write**, applied to the bucket `omts-files` only.
- Copy the **Access Key ID**, the **Secret Access Key** and the **Account ID** shown on that page. The secret is shown once.

Or with Wrangler:
```bash
npx wrangler login
npx wrangler r2 bucket create omts-files
```
The S3 access keys are only made in the dashboard (R2 API token).

Use one bucket per environment (`omts-files-staging`, `omts-files-prod`): the orphan file clean-up lists and deletes keys,
so two environments must never share a bucket.

**CORS on the bucket** is only needed if a page reads files with `fetch()`. `<img>` tags with the pre-signed link need none.

## 2. Configure the API

Environment variables (Docker / EC2 `.env`; never commit them):

| Variable | Value |
|---|---|
| `Storage__Provider` | `R2` |
| `Storage__R2__AccountId` | Cloudflare Account ID |
| `Storage__R2__AccessKeyId` | Access Key ID of the R2 API token |
| `Storage__R2__SecretAccessKey` | Secret Access Key of the R2 API token |
| `Storage__R2__Bucket` | `omts-files-staging` (one per environment) |
| `Storage__R2__ServiceUrl` | empty; set `https://<AccountId>.eu.r2.cloudflarestorage.com` only for an EU-jurisdiction bucket |

`Storage__Local__*` is not used with `R2`.

### Staging VPS (deploy/staging)

`deploy/staging/compose.yml` already sets `Storage__Provider: R2` and reads the keys from `/opt/omts/.env` on the VPS:
```
R2_ACCOUNT_ID=REPLACE_ME
R2_ACCESS_KEY_ID=REPLACE_ME
R2_SECRET_ACCESS_KEY=REPLACE_ME
R2_BUCKET=omts-files-staging
```
Write these lines into `.env` **before** CD deploys the compose change: the `:?` checks make `compose config` fail without
them, and `deploy.sh` then rolls back. Keep `STORAGE_SIGNING_KEY` in `.env` for a rollback to `Local`.
The `omts-files` volume of the API is then no longer needed for uploads; keep it until the copy below is done.
After the deploy, check `GET /health`.

## 3. Move the files already uploaded (Local → R2)

The database rows keep the storage **keys** (`{category}/{yyyy}/{MM}/{guid}.{ext}`), not URLs. So moving is a copy under the same
keys: no row changes. From the host, with the AWS CLI (the VPS has none: run the `amazon/aws-cli` image) and the same R2 keys:
```bash
export AWS_ACCESS_KEY_ID=<R2 access key id> AWS_SECRET_ACCESS_KEY=<R2 secret>
docker cp $(docker compose ps -q api):/app/App_Data/files ./files-backup
aws s3 sync ./files-backup s3://omts-files --endpoint-url https://<AccountId>.r2.cloudflarestorage.com --region auto
```
Switch the provider after the copy (a file uploaded between copy and switch would stay local; copy again after the switch to be
sure, `sync` only sends what is missing).

## 4. Check

- Upload a photo in the web app (e.g. a water measurement) and open it: its link starts with `https://<AccountId>.r2.cloudflarestorage.com/omts-files/...` and expires.
- The public provenance page (`/origin/{qrToken}`) shows lot photos: the browser loads the R2 link directly.
- `docker compose logs api | grep "R2 "` shows no upload or read errors.

## Rollback

Set `Storage__Provider=Local` again: files uploaded while on R2 are only in R2, so copy them back first
(`aws s3 sync s3://omts-files ./files ...`, then `docker cp` into the volume).
