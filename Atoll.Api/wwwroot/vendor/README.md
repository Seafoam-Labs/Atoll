# Vendored third-party assets

Committed verbatim; update by re-downloading the pinned files and bumping the versions here.

| File | Version | Source |
| --- | --- | --- |
| `highlight.min.js` (common bundle, ~40 languages) | 11.11.1 | <https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js> |

highlight.js is BSD-3-Clause (c) 2006-2024 Josh Goebel and contributors; the file keeps its
upstream license header. `Components/FileViewer.razor` pins its language map to the languages
shipped in this bundle. The code-viewer theme is not vendored - it is first-party, built from
Atoll's `@theme` tokens in `wwwroot/app.css` ("Code viewer" section).
