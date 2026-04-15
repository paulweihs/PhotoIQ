## ADR: Subject Recognition — "That's Bubba"

### Decision
PhotoIQ v2 will support subject recognition beyond
human faces — including pets, buildings, and objects.

### The Origin Story
A Yorkshire Terrier named Bubba. A photo mosaic made
from family memories. The AI described "a woman in a
red dress in a forest."

Bubba deserved better. So does every pet, place, and
person in every family library.

### How It Works
1. User opens any photo containing Bubba
2. User clicks subject → types "Bubba"
3. PhotoIQ generates a visual embedding for that subject
4. PIQ searches library for visual matches
5. Presents candidates: "Are these all Bubba?"
6. User confirms or rejects each — selector UI
7. Confirmed matches tagged "Bubba" permanently
8. Every future import checked against known subjects

### Scope — Not Just Faces
- People → "That's Kim"
- Pets → "That's Bubba"
- Buildings → "That's our house"
- Objects → "That's Dad's car"

### Why This Matters
> Tag one photo. Find them all.
> No cloud. No upload. No one else's algorithm.
> Your memories recognized on your machine.

### This is why the product exists.
