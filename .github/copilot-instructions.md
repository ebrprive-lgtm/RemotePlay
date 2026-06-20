# Copilot Instructions

## Project Guidelines
- When modifying the web card UI, verify that movie cards remain clickable and playback launch behavior is not regressed.
- When adjusting the music player UI, preserve queue functionality; only remove the empty visual 'Up Next' box, not the underlying queue display card/behavior.
- When requesting a UI fix scoped to grid view, do not alter list view behavior.

## Album Art Lookup
- For Discogs album-art lookup, use search queries in '<Artist> - <Title>' format and strip year prefixes from album titles (e.g., '2016 - Klein geluk' -> 'Klein geluk').