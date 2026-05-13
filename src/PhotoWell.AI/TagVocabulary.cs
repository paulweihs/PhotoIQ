using PhotoWell.Core.Models;

namespace PhotoWell.AI;

/// <summary>
/// Candidate tag prompts used for CLIP zero-shot classification.
/// Each entry pairs a natural-language prompt (fed to the text encoder) with
/// a short display label and its category.
/// </summary>
public static class TagVocabulary
{
    public static readonly IReadOnlyList<(string Prompt, string Label, TagCategory Category)> Entries =
    [
        // Objects
        // "a photo of a person" is intentionally broad — CLIP accurately anchors on human faces/bodies.
        // More restrictive wording degrades recall on partial or crowd shots.
        ("a photo of a person showing a human face or body", "person", TagCategory.Object),
        // Guard: requires multiple clearly-visible people, not a couple or solo portrait.
        ("a photo of a group of three or more people gathered together", "group", TagCategory.Object),
        ("a photo of a dog", "dog", TagCategory.Object),
        ("a photo of a cat", "cat", TagCategory.Object),
        // "car" kept broad — wheel/window textures are reliable discriminators.
        ("a photo of a car or truck on a road or parking lot", "car", TagCategory.Object),
        ("a photo of a bicycle", "bicycle", TagCategory.Object),
        // Anchor on blooms/petals to avoid firing on green foliage or fabric prints.
        ("a photo of flower blossoms or petals in close detail", "flower", TagCategory.Object),
        // Anchor on trunk/canopy to avoid triggering on indoor potted plants or wooden furniture.
        ("a photo of a tree with a visible trunk and branches outdoors", "tree", TagCategory.Object),
        // Require an exterior view so CLIP doesn't fire on interior walls.
        ("a photo of a building's exterior — facade, entrance, or architectural structure", "building", TagCategory.Object),
        // Require plated/prepared food on a table; avoids ingredient/supermarket shots.
        ("a photo of prepared food on a plate or serving dish", "food", TagCategory.Object),
        ("a photo of a whole roasted turkey on a platter", "turkey", TagCategory.Object),
        ("a photo of a bird — wild or domesticated — with feathers clearly visible", "bird", TagCategory.Object),
        ("a photo of an airplane in flight or on a runway", "airplane", TagCategory.Object),
        ("a photo of a boat or ship on water", "boat", TagCategory.Object),

        // Scenes
        // Beach: sandy shore + water — prevents firing on sandy playgrounds or sand art.
        ("a photo of a sandy beach with ocean or lake water visible", "beach", TagCategory.Scene),
        // Mountains: snow-capped peaks or ridgeline skyline — not just any hill.
        ("a photo of mountain peaks or a mountain range skyline", "mountains", TagCategory.Scene),
        // Forest: dense canopy of trees, not a single tree or a park.
        ("a photo of a dense forest or woodland with many trees and foliage", "forest", TagCategory.Scene),
        // City: recognisable urban skyline with multiple buildings — not a single storefront.
        ("a photo of a city skyline with multiple tall buildings or skyscrapers", "city", TagCategory.Scene),
        // Sunset: gradient sky with sun near horizon — warm-toned indoor lighting is not sufficient.
        ("a photo of a sunset or sunrise with the sun near the horizon and a coloured sky outdoors", "sunset", TagCategory.Scene),
        // Street: paved road with sidewalks/street signs — not a hallway or corridor.
        ("a photo of an outdoor street or road with pavement, sidewalks, or street signs", "street", TagCategory.Scene),
        // Ocean: open water to horizon — lake or pool not sufficient.
        ("a photo of the open ocean with waves and a water horizon", "ocean", TagCategory.Scene),
        ("a photo taken indoors inside a room or building", "indoor", TagCategory.Scene),
        ("a photo taken outdoors in natural or urban open air", "outdoor", TagCategory.Scene),
        // Park: landscaped green space with grass/paths — avoids firing on indoor plants.
        ("a photo of an outdoor park with grass, trees, and open space", "park", TagCategory.Scene),
        ("a photo of a kitchen with counters or cabinets", "kitchen", TagCategory.Scene),
        // Snow: outdoor snow cover on ground/trees — white cakes, walls, or bright fabric are not sufficient.
        ("a photo of snow covering the ground, trees, or rooftops outdoors", "snow", TagCategory.Scene),
        // Rain: already explicit; keep and clarify that indoor wet scenes are excluded.
        ("a photo of rain falling outdoors — wet pavement, puddles, or raindrops visible outside, not indoors", "rain", TagCategory.Scene),
        // Lake: calm inland body of water with shoreline — not a swimming pool.
        ("a photo of a calm lake or reservoir with a visible shoreline and natural surroundings", "lake", TagCategory.Scene),
        // River: flowing water between natural banks — not a fountain or canal.
        ("a photo of a river or stream with flowing water between natural banks", "river", TagCategory.Scene),

        // Activities
        ("a photo of people eating food at a table with plates or utensils visible", "eating", TagCategory.Activity),
        ("a photo of people dancing together at a party or on a dance floor", "dancing", TagCategory.Activity),
        // Running: require outdoor context or race-number bib to avoid posture-only matches.
        ("a photo of people running outdoors in a race or jogging on a trail or road", "running", TagCategory.Activity),
        // Behavioural activities — higher CLIP threshold applied in ClipTaggingService.
        // Removed: travel (fires on any social scene), family (unverifiable), wedding (fires on
        // any two people), night (fires on artificially-lit indoor rooms), birthday (removed earlier).
        ("a photo of someone carving a roasted turkey or roast with a knife or electric knife", "carving", TagCategory.Activity),
        // Graduation: require both cap and gown together — solid colours alone are not sufficient.
        ("a photo of a graduation ceremony with people wearing mortarboard caps and academic gowns", "graduation", TagCategory.Activity),
        // Sports: require visible equipment/ball/court — sitting or standing on a field is not sufficient.
        ("a photo of an active sports game with players in motion, a ball, or recognisable athletic equipment", "sports", TagCategory.Activity),
        // Concert: require stage, performers, or audience with instruments — a microphone alone is not sufficient.
        ("a photo of a live music concert or stage performance with performers and audience", "concert", TagCategory.Activity),
        // Hiking: require trail/pack/boots outdoors — walking on pavement is not sufficient.
        ("a photo of people hiking or trekking on a trail outdoors with a backpack or boots visible", "hiking", TagCategory.Activity),
        // Swimming: require open water or a pool with the person clearly in the water — bathtub shots are excluded.
        ("a photo of someone swimming in a pool, lake, or ocean — submerged or splashing in open water", "swimming", TagCategory.Activity),
        // Working: manual labour or desk work with clear task evidence — resting posture is not sufficient.
        ("a photo of someone visibly working — using tools, typing at a desk, or doing manual labour", "working", TagCategory.Activity),
        // "renovation" removed — fires on any domestic indoor scene (wood floors, furniture, living rooms).
        // "children playing" removed — fired on adult males; demographic fabrication is not recoverable.
        // Gardening: require garden tools or plant beds — touching houseplants is not sufficient.
        ("a photo of someone gardening outdoors with soil, plants, or garden tools visible", "gardening", TagCategory.Activity),
        // Camping: require tents in a natural setting — backyard BBQs are not sufficient.
        ("a photo of a camping scene with tents or campfire in a natural outdoor setting", "camping", TagCategory.Activity),
        // School: require a classroom, chalkboard, desks, or students — any lecture room counts.
        ("a photo of a school classroom or lecture hall with desks, a board, or students", "school", TagCategory.Activity),

        // ── Occasion tags (TagCategory.Event) ────────────────────────────────────────────────────
        // Require EXPLICIT visual evidence: specific food, decorations, or clear cultural markers.
        // A table with food and people is NOT sufficient — use OccasionConfidenceThreshold (0.30).
        // "holiday dinner" removed: "festive" is not a visual signal CLIP can resolve; fires on
        //   any gathering. Removed: wedding (fires on any two people in nice clothes).
        ("a photo of a whole roasted turkey at a Thanksgiving dinner — as a centerpiece on the table or being carved", "thanksgiving", TagCategory.Event),
        ("a photo with unmistakable Christmas decorations — a decorated tree with ornaments, Christmas lights on a house, stockings hung by a fireplace, Santa Claus, or wrapped gifts under a tree", "christmas", TagCategory.Event),
        // Halloween: require costumes or jack-o-lanterns — orange pumpkins alone or autumn foliage are not sufficient.
        ("a photo of Halloween costumes being worn or carved jack-o-lanterns displayed", "halloween", TagCategory.Event),

        // Style / Composition
        // Portrait: require a person as the primary subject filling most of the frame.
        ("a close portrait photo of a person's face or upper body as the primary subject", "portrait", TagCategory.Style),
        // Landscape: wide-angle outdoor scene — indoor room shots with windows are not sufficient.
        ("a wide landscape photo of an outdoor natural or urban scene with a broad horizon", "landscape", TagCategory.Style),
        // Macro: extreme close-up with shallow depth of field showing fine texture or detail.
        ("a close-up macro photo showing fine detail or texture with shallow depth of field", "macro", TagCategory.Style),
        ("a black and white photo", "black and white", TagCategory.Style),
        ("an aerial photo taken from directly above or a high angle — drone or plane view", "aerial", TagCategory.Style),
        // Long exposure: light trails or motion blur from moving lights — not HDR glow.
        ("a long exposure photo with light trails from moving vehicles or stars in the night sky", "long exposure", TagCategory.Style),
        // Minimalist: intentional empty space and sparse composition — not underexposed or cropped shots.
        ("a minimalist photo with deliberate empty space and a sparse, simple composition", "minimalist", TagCategory.Style),
        // Dramatic: strong shadows/highlights from intentional lighting — not just a dark room.
        ("a dramatic photo with strong contrast between deep shadows and bright highlights", "dramatic", TagCategory.Style),

        // Color / Tone
        // Warm tones: intentional warm colour grading — not just any tungsten-lit interior.
        ("a photo with warm golden or amber tones from sunlight or deliberate colour grading", "warm tones", TagCategory.Color),
        // Cool tones: deliberate cool/blue cast — not just an overcast sky.
        ("a photo with cool blue or teal tones from shade, water reflection, or deliberate colour grading", "cool tones", TagCategory.Color),
        ("a photo with vibrant, highly saturated colors", "vibrant", TagCategory.Color),
    ];
}
