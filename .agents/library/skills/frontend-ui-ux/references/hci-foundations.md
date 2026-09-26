# HCI foundations — primary sources behind Pillar 1

Read this when you want the original citation behind a heuristic or law, or need to justify a UI decision to someone who will ask "says who?"

## Heuristic evaluation

Jakob Nielsen and Rolf Molich developed heuristic evaluation in **1990**; Nielsen refined it in **1994** using a factor analysis of 249 real usability problems to derive the set with maximum explanatory power. The wording has been lightly updated since (most recently 2020) but the 10 heuristics themselves are unchanged since 1994. They are deliberately broad rules of thumb, not strict interface law — hold them up against any product to spot where users are likely to get lost, frustrated, or surprised. Canonical reference: [Nielsen Norman Group, "10 Usability Heuristics for User Interface Design"](https://www.nngroup.com/articles/ten-usability-heuristics/).

## The quantitative laws

**Fitts's Law** — Fitts, P.M. (1954), "The Information Capacity of the Human Motor System in Controlling the Amplitude of Movement," *Journal of Experimental Psychology*. Modeled pointing movement using Shannon–Weaver information theory: the time to acquire a target is a function of the distance to it and its size. In HCI terms — bigger, closer targets get hit faster and with fewer errors. This is the actual math behind minimum touch-target sizing and behind putting frequent actions near the cursor's likely resting position (e.g., screen edges and corners are effectively infinite targets, since the cursor can't overshoot past them). Modern walkthrough: [NN/g, "Fitts's Law and Its Applications in UX"](https://www.nngroup.com/articles/fitts-law/).

**Hick's Law / the Hick–Hyman Law** — Hick, W.E. (1952) and Hyman, R. (1953), independently, in the *Quarterly Journal of Experimental Psychology*. Choice reaction time increases with the logarithm of the number of alternatives. Card, Moran, and Newell (1983) later packaged both Fitts's and Hick's laws as explicit design tools for interface developers in *The Psychology of Human-Computer Interaction*, introducing the GOMS model (Goals, Operators, Methods, Selection rules) as the first serious attempt to formally model human task performance for interface design.

**Miller's Law** — Miller, G.A. (1956), "The Magical Number Seven, Plus or Minus Two: Some Limits on Our Capacity for Processing Information," *Psychological Review*. One of the most cited papers in psychology; commonly (if imprecisely) read as: short-term/working memory holds about seven "chunks" of information. The practical UI takeaway is about chunking, not the literal number seven — group related information so the mental unit count stays low, regardless of how many individual fields or facts are inside each chunk.

**Jakob's Law** — informally attributed to Jakob Nielsen: "users spend most of their time on other sites/products, so they prefer yours to work the same way." Not a single paper — a distillation of decades of usability-testing observation that user expectations transfer between products. It's the argument for platform convention over novelty by default.

**Peak–End Rule** — from Daniel Kahneman's behavioral-economics research on how people remember experiences: judgment of a past experience correlates with its most intense point and its ending, not the time-weighted average of the whole thing. Applied to product design, this is why the last screen of a checkout flow and the wording of an error message matter disproportionately more than their frequency of appearance would suggest.

## Where to keep digging

- [Laws of UX](https://lawsofux.com/) (Jon Yablonski) — an illustrated, continuously-referenced collection of these and other principles, each traced back to its source.
- [ACM Digital Library](https://dl.acm.org/) — search "CHI conference" for current, peer-reviewed primary HCI research rather than secondary summaries.
- [Nielsen Norman Group](https://www.nngroup.com/) — ongoing applied research grounded in actual usability-test sessions, not opinion pieces.
- [Baymard Institute](https://baymard.com/) — large-scale (130,000+ hour) UX benchmarking across major ecommerce sites; useful when a decision needs to be justified by measured conversion/usability impact rather than a heuristic alone.
