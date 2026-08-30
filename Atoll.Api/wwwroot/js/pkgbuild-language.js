/* Registers a PKGBUILD grammar with the self-hosted highlight.js, derived at runtime from the
   bundle's own bash language (no bundler/Node toolchain in this repo). Bash already styles
   function definitions - package(), pkgver(), split-package package_foo() - through its
   FUNCTION mode, but never styles assignment left-hand sides, so the only addition is a mode
   for the PKGBUILD(5) fields. The (?==) guard leaves shell usage of shared names (source,
   install, arch ...) untouched, and the optional _suffix covers split-package overrides like
   depends_foo=. No-op if highlight.js or its bash language failed to load. */
(function () {
  "use strict";

  if (typeof hljs === "undefined" || !hljs.getLanguage("bash")) {
    return;
  }

  var fields = [
    "pkgbase",
    "pkgname",
    "pkgver",
    "pkgrel",
    "epoch",
    "pkgdesc",
    "url",
    "arch",
    "license",
    "groups",
    "depends",
    "makedepends",
    "checkdepends",
    "optdepends",
    "provides",
    "conflicts",
    "replaces",
    "backup",
    "options",
    "install",
    "changelog",
    "source",
    "noextract",
    "validpgpkeys",
    "md5sums",
    "sha1sums",
    "sha224sums",
    "sha256sums",
    "sha384sums",
    "sha512sums",
    "b2sums",
  ];

  hljs.registerLanguage("pkgbuild", function () {
    // Derive from a fresh, uncompiled bash grammar (the factory registerLanguage keeps
    // as rawDefinition), not from the live compiled language: languages compile lazily
    // and in place, so sharing their mode objects across registrations breaks once
    // parsing and registration interleave. The factory gives pkgbuild its own tree.
    var bash = hljs.getLanguage("bash").rawDefinition();

    return {
      name: "PKGBUILD",
      keywords: bash.keywords,
      contains: [
        {
          scope: "variable",
          begin: new RegExp(
            "\\b(?:" + fields.join("|") + ")(_[A-Za-z0-9_]+)?(?==)",
          ),
        },
      ].concat(bash.contains),
    };
  });
})();
