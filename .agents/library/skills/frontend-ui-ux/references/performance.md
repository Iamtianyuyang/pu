# Performance budgets — Core Web Vitals and beyond

Read this when working on anything load-bearing for perceived speed: a hero/landing section, an image-heavy page, a long list, or when investigating a reported "feels slow" complaint.

## The three Core Web Vitals

Source: [web.dev, "Web Vitals"](https://web.dev/articles/vitals). Measured at the **75th percentile** of real page loads — a site only counts as "good" on a metric if at least 75% of visits meet the threshold.

| Metric | Measures | Good | Needs improvement | Poor |
|---|---|---|---|---|
| **LCP** (Largest Contentful Paint) | Perceived load speed — when the main content has likely rendered | ≤ 2.5s | 2.5s–4.0s | > 4.0s |
| **INP** (Interaction to Next Paint) | Responsiveness — delay between a user interaction and the next visual update | ≤ 200ms | 200ms–500ms | > 500ms |
| **CLS** (Cumulative Layout Shift) | Visual stability — how much visible content unexpectedly moves | ≤ 0.1 | 0.1–0.25 | > 0.25 |

## CLS discipline (visual stability)

- Reserve space for every image, embed, iframe, and ad slot with an explicit `width`/`height` (or `aspect-ratio` in CSS) **before** it loads — never let intrinsic size be discovered only after the asset arrives.
- Never inject new content above existing content after initial render (a banner that pushes everything down once it loads is a CLS violation even if it's "just for a second").
- Load web fonts with `font-display: swap` (or `optional` if the fallback is close enough) and preload the exact weights actually used — an invisible font swap that changes line height/character width is a common, easy-to-miss CLS source.
- Animate with `transform` and `opacity` only where possible; animating `width`/`height`/`top`/`left` triggers layout on every frame.

## LCP discipline (load speed)

- Identify the actual largest above-the-fold element (usually a hero image, a large heading, or a hero video poster) and treat it as the critical resource: `<link rel="preload">` it, mark images `fetchpriority="high"`, and never lazy-load it.
- Don't gate the LCP element behind a client-side data fetch if it can be server-rendered or statically known.
- Serve responsive images (`srcset`/`sizes`) at the resolution the layout actually needs — shipping a 4000px-wide source image into a 400px-wide slot is a common, large, avoidable LCP cost.
- Self-host or preconnect to third-party font/analytics/embed origins; every extra DNS lookup and connection adds latency before the critical resource can even start downloading.

## INP discipline (responsiveness)

- Keep the main thread free: break long JavaScript tasks (>50ms) into smaller chunks so user input isn't queued behind them.
- Debounce or throttle expensive input handlers (search-as-you-type, scroll listeners, resize listeners).
- Avoid layout thrashing — don't interleave DOM reads and writes in a loop (read all needed layout values first, then write).
- Move genuinely expensive computation off the main thread (a Web Worker) when it's not trivially small.

## General weight budget

- Code-split by route; don't ship a component's JavaScript to a page that never renders it.
- Lazy-load below-the-fold and off-screen content (images with native `loading="lazy"`, deferred non-critical scripts).
- Question any new dependency added for something a few lines of first-party code could do — bundle size is a recurring, compounding tax, not a one-time cost.
- Compress and correctly size all images (modern formats — WebP/AVIF — where supported, with a fallback).

## Verification method

Don't estimate performance from reading the code — measure it. Use browser DevTools' Performance/Lighthouse panels (or `web.dev/measure`) against a realistic, throttled network (Slow 4G) and CPU (4x–6x slowdown) profile, since a fast local dev machine on fiber hides nearly every real-world performance problem.
