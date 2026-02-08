<div align="center">
  <h1>📚 Bookworm</h1>
  <p>A Readarr-inspired book companion powered by Open Library & Hardcover. Now in BETA 0.1</p>
  <p>
    <img src="https://img.shields.io/badge/.NET-8.0-5C2D91?style=flat-square" alt=".NET 8 badge" />
    <img src="https://img.shields.io/badge/UI-Minimal%20API%20SPA-2563EB?style=flat-square" alt="Minimal SPA badge" />
  </p>
</div>

# Bookworm

Bookworm is a **Readarr-inspired book companion** that helps you discover, track, and manage books you want to read.  
It’s powered by **Open Library** for discovery/search and **Hardcover** for personal integration, with have also included **Calibre** for library syncing.

> Bookworm is **not a downloader and does not directly intergrate with a torent client or indexer**.
> If you want automated downloads, use Calibre’s web automation.

> You must have a hardcover api key and calibre to have this app working. We are currently exploring other platforms to make this more optional rather than mandatory.

---

## What Bookworm Does

- **Discover books** via Open Library:
  - Search by **title**, **author**, or **ISBN**
  - View results in a modern, card-based UI with cover art
- **Manage a Wanted list** (books you’re looking for / planning to read)
- **Hardcover “Want to Read” integration**:
  - Sync your personal Hardcover “Want to Read” shelf using your own API key
  - Hardcover is also used to enrich metadata and suggestions
- **Calibre integration**:
  - Reads directly from your Calibre **metadata.db**
  - Lets Bookworm understand what you already own so you don’t re-want duplicates
- **Optional Calibre + Anna’s Archive helper**:
  - If you want a Calibre plugin that can cross-reference Anna’s Archive, check out: https://github.com/soupofstars/calibre_annas_archive

---

## Key Features

- 🔎 **Fast search** by author / ISBN through Open Library  
- 📚 **Wanted list** for books you plan to get/read  
- 🔗 **Hardcover shelf sync** (personal “Want to Read”)  
- 🗂️ **Calibre library awareness**  
- 🧼 **Clean, modern UI** using responsive cards and cover images  

---

## Requirements

- **Hardcover API key is required** for full functionality.  
  Without it, Hardcover sync and enrichment won’t work.
- **Internet access** for Open Library + Hardcover queries
- **Optional:** Calibre installed locally (only if you want library sync)

---

## Configuration

Bookworm looks for the following configuration (environment variables or `appsettings.json`, depending on your setup):

- `Hardcover__ApiKey` – **required**  
- `Hardcover__Endpoint` – optional (defaults to Hardcover GraphQL endpoint)
- `OpenLibrary__Endpoint` – optional (defaults to Open Library API)
- `Calibre__MetadataPath` – optional path to Calibre `metadata.db`

Example `.env`:

```env
Hardcover__ApiKey=YOUR_KEY_HERE
Hardcover__Endpoint=https://api.hardcover.app/v1/graphql
OpenLibrary__Endpoint=https://openlibrary.org
Calibre__MetadataPath=/path/to/Calibre/metadata.db

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

## Calibre Plugin API (Wanted list)

If you are building a Calibre plugin and need to know which books are marked as **Wanted** in Bookworm, use the read-only endpoint below. It returns a normalized list with titles and ISBN candidates so you can match safely inside Calibre.

- `GET /api/calibre/wanted`
- Sample response:
```json
{
  "count": 1,
  "items": [
    {
      "key": "the-hobbit",
      "title": "The Hobbit",
      "authors": ["J.R.R. Tolkien"],
      "isbns": ["9780547928227", "054792822X"],
      "source": "openlibrary",
      "slug": "/works/OL263319W"
    }
  ]
}
```

## 🤝 Contributing

1. Fork & branch.
2. `dotnet run` to verify changes.
3. Submit a PR with screenshots/GIFs for UI tweaks.

## 📜 License

MIT © 2024 Your Name. See [LICENSE](LICENSE) for details.
