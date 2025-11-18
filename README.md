# bookworm

A rehash of the Readarr idea, but making it better. Using OpenLibrary API to search at the moment, and updating with new code everyday.

This is by no means suitable for use yet - Written on Apple Silcone not compiled for x86

Currently Working on:
  OpenLibrary API Calls and search logic
  API intergration with Hardcover.app using personal bearer tokens.
  Graphical alterations to blend with readarr

You must setup an account on Hardcover.app to get your API Key.

Docker:
docker run -d \
 --name bookworm \
 -p 8787:8080 \
 -e Hardcover\_\_ApiKey="YOUR_REAL_HARDCOVER_API_KEY" \
 bookworm
