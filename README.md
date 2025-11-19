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
- **Modern UI** with card layout, hoverable covers, lightbox previews, and expandable author chips linking to the in-app author search.
- **Calibre placeholder** ready for future integrations.

## 🚀 Getting Started

```bash
git clone https://github.com/your-user/bookworm.git
cd Bookworm
dotnet run --project Bookworm.csproj
```

Open your browser at `http://localhost:5099`.

### Docker

```bash
docker run -d \
  --name bookworm \
  -p 8787:8080 \
  -e Hardcover__ApiKey="YOUR_REAL_HARDCOVER_API_KEY" \
  ghcr.io/your-user/bookworm:latest
```

### Configuration

| Key | Description |
| --- | --- |
| `Hardcover:ApiKey` / `Hardcover__ApiKey` | Personal Hardcover token (required for Hardcover tab). |
| `Hardcover:Endpoint` | Optional override of the GraphQL endpoint. |

## 🧱 Project Structure

| Path | Description |
| --- | --- |
| `Program.cs` | Minimal API, HttpClient registration, static file hosting. |
| `wwwroot/index.html` | SPA markup. |
| `wwwroot/site.css` | Styling (cards, nav, lightbox). |
| `wwwroot/js/main.js` | Shared state, card rendering, lightbox, navigation. |
| `wwwroot/js/search-books.js` | Book + ISBN search logic. |
| `wwwroot/js/search-authors.js` | Author search logic. |
| `wwwroot/js/hardcover.js` | Hardcover “want to read” fetcher. |

## 🛣️ Roadmap

- Persistent storage for Library/Wanted.
- Calibre sync API.
- Offline cache + multi-user auth.
- Automated tests / CI.

## 🤝 Contributing

1. Fork & branch.
2. `dotnet run` to verify changes.
3. Submit a PR with screenshots/GIFs for UI tweaks.

## 📜 License

MIT © 2024 Your Name. See [LICENSE](LICENSE) for details.
