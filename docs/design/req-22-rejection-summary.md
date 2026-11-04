# REQ-22 reception rejection summary

This extends the existing reception page in the selected Vigiloom visual style. An administrator can see the three fixed rejection categories, persisted counts and one representative ingress/source when authentication established that source. Pending approval is not labelled an attack. The brief explanation stays visible; the expanded explanation distinguishes global counts, representative examples and possible loss during restart or storage failures.

The [desktop capture](evidence/req-22-rejections/desktop.png) uses a 1440 × 1024 CSS viewport and the [mobile capture](evidence/req-22-rejections/mobile.png) uses a 390 × 844 CSS viewport, both at device scale 1 with isolated synthetic data. The browser test exercises the explanation disclosure and horizontal table region. The new summary is an extension of the existing page, not a pixel-matched implementation of a separate source mockup. Screen-reader testing has not been performed.
