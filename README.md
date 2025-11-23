<div align="center">
  <h1>📚 Bookworm</h1>
  <p>A Readarr-inspired book companion powered by Open Library & Hardcover.</p>
  <p>
    <img src="https://img.shields.io/badge/.NET-8.0-5C2D91?style=flat-square" alt=".NET 8 badge" />
    <img src="https://img.shields.io/badge/UI-Minimal%20API%20SPA-2563EB?style=flat-square" alt="Minimal SPA badge" />
  </p>
</div>

## ✨ Features

- **Search Books, Authors, ISBNs** via Open Library with match/weak/suggested groupings.
- **Library & Wanted Shelves** to keep quick lists (in-memory for now).
- **Hardcover "Want to Read" integration** using your personal API key.
- **Hardcover people-list picks** surfaced from your Calibre titles (find what other readers list alongside your books).
- **Modern UI** with card layout, hoverable covers, lightbox previews, and expandable author chips linking to the in-app author search.


### Calibre metadata (SQLite)

Bookworm can read directly from Calibre’s `metadata.db` (a SQLite database). Point the app at that file by editing `Calibre:DatabasePath`.

- Local dev: set the absolute path, e.g. `/Users/you/Calibre Library/metadata.db`.
- Docker: bind-mount the folder and point the env var at the mounted location (see the example below). Without the volume mount, the container cannot see your host’s SQLite database.
- After configuring the path, open the Calibre tab and click **Sync Calibre** to mirror the library into Bookworm’s local database.
- Currently tested with version 8.4.0

### Docker

```bash
docker run -d \
  --name bookworm \
  -p 8787:8080 \
  -e Hardcover__ApiKey="YOUR_REAL_HARDCOVER_API_KEY" \
  -v ~/bookworm-data:/data \
  -e Storage__Database='Data Source=/data/bookworm.db' \
  -v "/Users/you/Calibre Library:/calibre" \
  -e Calibre__DatabasePath="/calibre/metadata.db" \
  bookworm
```

### Configuration

| Key | Description |
| --- | --- |
| `Hardcover:ApiKey` / `Hardcover__ApiKey` | Personal Hardcover token (required for Hardcover tab). |
| `Calibre:DatabasePath` | Full path to your Calibre `metadata.db` for local sync. |




## 🛣️ Roadmap

- Calibre sync API.
- Offline cache.
- Automated tests / CI.

## 🤝 Contributing

1. Fork & branch.
2. `dotnet run` to verify changes.
3. Submit a PR with screenshots/GIFs for UI tweaks.

## 📜 License

MIT © 2024 Your Name. See [LICENSE](LICENSE) for details.
