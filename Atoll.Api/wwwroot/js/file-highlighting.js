/* Client-side syntax coloring for the static SSR file viewer. Blazor replaces page content
   during enhanced navigation and then raises its enhancedload event, so no DOM-wide mutation
   observer is needed. Stays a no-op if the vendored highlight.js script failed to load. */
(function () {
  "use strict";

  if (typeof hljs === "undefined") {
    return;
  }

  // Larger files remain plain text to avoid blocking the main thread during highlighting.
  var charCap = 128 * 1024;

  function highlightViewers() {
    var viewers = document.querySelectorAll(".code-body code");
    for (var i = 0; i < viewers.length; i++) {
      var element = viewers[i];
      var text = element.textContent || "";

      // Enhanced navigation may reuse the element. Restore its plain-text state before
      // highlighting the newly rendered content, or before leaving a large file uncolored.
      element.textContent = text;
      element.classList.remove("hljs");
      element.removeAttribute("data-highlighted");

      if (text.length <= charCap) {
        hljs.highlightElement(element);
      }
    }
  }

  function registerEnhancedLoadHandler() {
    if (typeof Blazor !== "undefined") {
      Blazor.addEventListener("enhancedload", highlightViewers);
    }
  }

  // Deferred scripts run after the initial SSR body has been parsed. Blazor normally starts
  // first because its non-deferred script follows these tags; the load fallback makes the
  // registration resilient if startup timing changes.
  highlightViewers();
  if (typeof Blazor !== "undefined") {
    registerEnhancedLoadHandler();
  } else {
    window.addEventListener("load", registerEnhancedLoadHandler, {
      once: true,
    });
  }
})();
