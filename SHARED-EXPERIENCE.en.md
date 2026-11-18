# File purpose experience sharing (beta)

[简体中文](SHARED-EXPERIENCE.md) · [Product guide](README.en.md) · [User guide](docs/releases/1.1-UserGuide.en.md)

Sharing is off by default. Read the agreement and review the sharing scope in the app before deciding whether to join. Sharing past analyses requires a separate choice. Local scans and your own configured AI service remain available when sharing is off.

## How matching works

After a scan, the app downloads the public path index and checks scan results locally. It groups matching normalized fingerprints before retrieving conclusions in batches. The size threshold for AI analysis does not apply to cloud matching. The service returns published conclusions only when the match requirements are met. Identical file names can have different purposes.

Matched conclusions show their cloud source. Other items can use valid personal history or your own AI service after you confirm what to submit. Downloading or matching experience does not call the model.

## Sharing and withdrawal

Known conclusions rated Medium or High confidence can enter the upload queue if they pass privacy checks. Shared data includes anonymized public fingerprints, structured categories and recommendations, filtered Chinese and English purpose text, and records needed for consent and withdrawal. The app does not upload file contents, model API keys, complete private paths, or raw scan lists.

You can exclude folders, disable sharing, and review or withdraw your contributions. Disabling sharing stops future queries and uploads. Previously uploaded contributions can be withdrawn separately. Keep recovery credentials private. DiskLens does not delete files automatically; AI and shared conclusions are guidance only.

The cloud service needs an internet connection and may be rate limited or unavailable. Cloudflare response times can vary on mainland China networks. Try again later. Report problems in [GitHub Issues](https://github.com/kxdyh/Disklens/issues) and remove private paths and keys from attachments.
