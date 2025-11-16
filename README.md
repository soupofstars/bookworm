# bookworm
A rehash of Readarr, but making it better.

This is by no means suitable for use yet

Currnetly Working on:
API intergration with Hardcover.app using personal bearer tokens.
Graphical alterations to blend with readarr

You must setup an account on Hardcover.app to get your API Key.

Docker:
docker run -d \
  --name bookworm \
  -p 8787:8080 \
  -e Hardcover__ApiKey="YOUR_REAL_HARDCOVER_API_KEY" \
  bookworm-api


