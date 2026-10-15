# Credits artwork

The Credits page uses this upstream artwork.

- `tmdb.svg`: the approved Primary short (blue) logo linked from
  <https://www.themoviedb.org/about/logos-attribution?language=it>:
  <https://www.themoviedb.org/assets/2/v4/logos/v2/blue_square_1-5bdc75aaebeb75dc7ae79426ddd9be3b2be1e342510f8202baf6bffa71d7f5c4.svg>.
  Its attribution requirements are at <https://developer.themoviedb.org/docs/faq>.
  The SVG's CSS class is expressed as an equivalent `fill` attribute because
  Direct2D ignores style elements and class selectors. Paths, gradient colors
  and proportions are unchanged; see
  <https://learn.microsoft.com/windows/win32/direct2d/svg-support>.

The installer owns distribution notices; their provenance is in
[installer/notices](../../../installer/notices/README.md).

## Peer map

`world.bin` comes from Natural Earth 5.1
[1:50m admin-0 countries](https://www.naturalearthdata.com/downloads/50m-cultural-vectors/50m-admin-0-countries-2/)
without Antarctica, and
[10m populated places](https://www.naturalearthdata.com/downloads/10m-cultural-vectors/10m-populated-places/)
with their `POP_MAX` populations. Each outline point stores its
Douglas-Peucker tolerance, computed once per shared border so both sides
match; points below 1/128° are left out. Anchors are Natural Earth label points;
Bonaire, Bouvet Island and the US Minor Outlying Islands have neither an
outline nor a place, so their anchors are Kralendijk, the island, and Wake
Island. Population weights are a visual proxy, not a density raster or evidence
of individual peers' locations.
Natural Earth makes the data [public domain](https://www.naturalearthdata.com/about/terms-of-use/).
