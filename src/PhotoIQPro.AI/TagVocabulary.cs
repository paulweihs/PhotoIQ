using PhotoIQPro.Core.Models;

namespace PhotoIQPro.AI;

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
        ("a photo of a person", "person", TagCategory.Object),
        ("a photo of a group of people", "group", TagCategory.Object),
        ("a photo of a dog", "dog", TagCategory.Object),
        ("a photo of a cat", "cat", TagCategory.Object),
        ("a photo of a car", "car", TagCategory.Object),
        ("a photo of a bicycle", "bicycle", TagCategory.Object),
        ("a photo of a flower", "flower", TagCategory.Object),
        ("a photo of a tree", "tree", TagCategory.Object),
        ("a photo of a building", "building", TagCategory.Object),
        ("a photo of food", "food", TagCategory.Object),
        ("a photo of a bird", "bird", TagCategory.Object),
        ("a photo of an airplane", "airplane", TagCategory.Object),
        ("a photo of a boat", "boat", TagCategory.Object),

        // Scenes
        ("a photo of a beach", "beach", TagCategory.Scene),
        ("a photo of mountains", "mountains", TagCategory.Scene),
        ("a photo of a forest", "forest", TagCategory.Scene),
        ("a photo of a city skyline", "city", TagCategory.Scene),
        ("a photo of a sunset", "sunset", TagCategory.Scene),
        ("a photo of a street", "street", TagCategory.Scene),
        ("a photo of the ocean", "ocean", TagCategory.Scene),
        ("a photo taken indoors", "indoor", TagCategory.Scene),
        ("a photo taken outdoors", "outdoor", TagCategory.Scene),
        ("a photo taken at night", "night", TagCategory.Scene),
        ("a photo of a park", "park", TagCategory.Scene),
        ("a photo of snow", "snow", TagCategory.Scene),
        ("a photo of rain", "rain", TagCategory.Scene),
        ("a photo of a lake", "lake", TagCategory.Scene),
        ("a photo of a river", "river", TagCategory.Scene),

        // Activities
        ("a photo of people eating", "eating", TagCategory.Activity),
        ("a photo of people dancing", "dancing", TagCategory.Activity),
        ("a photo of people running", "running", TagCategory.Activity),
        ("a photo of a wedding", "wedding", TagCategory.Activity),
        ("a photo of a birthday party", "birthday", TagCategory.Activity),
        ("a photo of a graduation ceremony", "graduation", TagCategory.Activity),
        ("a photo of travel and tourism", "travel", TagCategory.Activity),
        ("a photo of sports", "sports", TagCategory.Activity),
        ("a photo of a concert", "concert", TagCategory.Activity),
        ("a photo of hiking", "hiking", TagCategory.Activity),
        ("a photo of swimming", "swimming", TagCategory.Activity),

        // Style / Composition
        ("a portrait photo", "portrait", TagCategory.Style),
        ("a landscape photo", "landscape", TagCategory.Style),
        ("a close-up macro photo", "macro", TagCategory.Style),
        ("a black and white photo", "black and white", TagCategory.Style),
        ("an aerial photo taken from above", "aerial", TagCategory.Style),
        ("a long exposure light trails photo", "long exposure", TagCategory.Style),
        ("a minimalist photo with empty space", "minimalist", TagCategory.Style),
        ("a dramatic high contrast photo", "dramatic", TagCategory.Style),

        // Color / Tone
        ("a photo with warm golden tones", "warm tones", TagCategory.Color),
        ("a photo with cool blue tones", "cool tones", TagCategory.Color),
        ("a photo with vibrant saturated colors", "vibrant", TagCategory.Color),
    ];
}
